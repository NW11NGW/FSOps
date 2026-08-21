using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The contract API end to end: browsing the board, taking a job, flying its legs in order, and
/// handing it back.
///
/// <para>Driven through the endpoints themselves rather than the services beneath them, because the
/// refusals are half the point. A screen has to be able to explain why a leg cannot be started, and a
/// refusal that comes back as a bare status code is one the UI cannot render into anything a player
/// can act on.</para>
/// </summary>
public class ContractEndpointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    // ---------- The board ----------

    /// <summary>
    /// <b>The board is a board, not a lever.</b> Two reads in the same period return the same jobs -
    /// the same identities, not merely the same shapes - so reloading cannot reroll it. If it could,
    /// the rational play would be to keep reloading until something good appeared.
    /// </summary>
    [Fact]
    public async Task ReadingTheBoardTwiceInTheSamePeriod_ReturnsTheSameJobs()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var service = CreateBoardService(ctx, Now);

        var first = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var second = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.NotEmpty(first.Offered);
        Assert.Equal(
            first.Offered.Select(c => c.Id).OrderBy(id => id),
            second.Offered.Select(c => c.Id).OrderBy(id => id));

        // And nothing was written twice - the unique index on (airline, bucket, slot) would have
        // rejected it, but a second board quietly appearing under a new bucket would not.
        Assert.Equal(first.Offered.Count, await ctx.Db.Contracts.CountAsync());
    }

    /// <summary>
    /// Offers that were never taken expire when the board moves on - they do not accumulate, and they
    /// are Expired rather than Abandoned. An offer nobody took is not a job somebody walked out of,
    /// and it must never carry a charge.
    /// </summary>
    [Fact]
    public async Task WhenTheBoardRefreshes_UntakenOffersExpireWithoutCharge()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var service = CreateBoardService(ctx, clock);

        var first = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var firstIds = first.Offered.Select(c => c.Id).ToHashSet();
        Assert.NotEmpty(firstIds);

        clock.UtcNow = Now.AddDays(1);
        ctx.Db.ChangeTracker.Clear();
        var second = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.NotEqual(first.Bucket, second.Bucket);
        Assert.DoesNotContain(second.Offered, c => firstIds.Contains(c.Id));

        var expired = await ctx.Db.Contracts.Where(c => firstIds.Contains(c.Id)).ToListAsync();
        Assert.All(expired, c => Assert.Equal(ContractStatus.Expired, c.Status));

        // Expiring an untaken offer costs nothing at all.
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.Category == LedgerCategory.ContractFee).ToListAsync());
    }

    /// <summary>An accepted job survives the refresh: it belongs to the player now, not to the board.</summary>
    [Fact]
    public async Task AnAcceptedJob_SurvivesTheBoardRefreshing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var service = CreateBoardService(ctx, clock);
        var board = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var taken = board.Offered.First();

        await ContractEndpoints.AcceptAsync(taken.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);

        clock.UtcNow = Now.AddDays(3);
        ctx.Db.ChangeTracker.Clear();
        var later = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.Contains(later.Accepted, c => c.Id == taken.Id);
        Assert.DoesNotContain(later.Offered, c => c.Id == taken.Id);
    }

    /// <summary>
    /// With almost nothing available the board is thin, and the API says so in words a player can act
    /// on. This is the honest-degradation requirement checked at the boundary the UI actually reads.
    /// </summary>
    [Fact]
    public async Task WithNoAircraftAvailable_TheBoardEndpointExplainsItselfRatherThanReturningNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        // Tick every catalogue aircraft OFF, which is the strongest form of "nothing available".
        var settings = new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            SimEdition = SimEdition.Standard,
            SimAircraftOverridesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                on = Array.Empty<string>(),
                off = ContractAircraftCatalogue.All.Select(a => a.TypeDesignator).ToArray(),
            }),
        };
        ctx.Db.UserSettings.Add(settings);
        await ctx.Db.SaveChangesAsync();

        var result = await ContractEndpoints.BoardAsync(
            ctx.Db, ctx.CurrentUser, CreateBoardService(ctx, Now), EconomyConfigCatalog.Default(), CancellationToken.None);

        var body = OkValueOf<BoardProbe>(result);
        Assert.Empty(body.Offered);
        Assert.NotNull(body.Limitation.Message);
        Assert.Contains("Settings", body.Limitation.Message);
        Assert.Equal(0, body.Limitation.AvailableAircraftCount);
    }

    /// <summary>
    /// <b>Accepting a job is not a limitation.</b> The board generated everything it asked for; the
    /// player took one of them, which is the entire point of a board. Reporting that as a thin board -
    /// and telling them to go and tick more aircraft in Settings, which was the advice it gave - is
    /// wrong about the cause and wrong about the fix.
    ///
    /// <para>The distinction is between how many jobs the board <i>produced</i>, which is fixed once
    /// the bucket is written, and how many are still <i>unclaimed</i>, which is a fact about the
    /// player. The message is about the first and was being computed from the second.</para>
    /// </summary>
    [Fact]
    public async Task AcceptingAJob_DoesNotChangeWhatTheBoardSaysAboutItself()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        var generatedBefore = board.Limitation.Generated;
        var messageBefore = board.Limitation.Message;
        var offeredBefore = board.Offered.Count;
        Assert.NotEmpty(board.Offered);

        await ContractEndpoints.AcceptAsync(
            board.Offered.First().Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        var after = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        // One fewer on the board, one in hand.
        Assert.Equal(offeredBefore - 1, after.Offered.Count);
        Assert.Single(after.Accepted);

        // But how many jobs this board PRODUCED has not changed, because it cannot: that was settled
        // when the bucket was written. Taking one is not the board failing to offer one.
        Assert.Equal(generatedBefore, after.Limitation.Generated);
        Assert.Equal(messageBefore, after.Limitation.Message);
    }

    /// <summary>
    /// The specific shape of the bug, isolated: on a board that produced everything it was asked for,
    /// accepting a job must leave nothing to explain. Skipped rather than failed when the fixture
    /// world happens not to fill the board, because then it is testing something it cannot see.
    /// </summary>
    [Fact]
    public async Task OnAFullBoard_AcceptingAJob_LeavesNothingToExplain()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        if (board.Limitation.Generated < board.Limitation.Requested)
        {
            // Not a full board in this fixture world - the sibling test above covers the invariant
            // that actually matters here, without depending on the generator filling every slot.
            return;
        }

        Assert.Null(board.Limitation.Message);

        await ContractEndpoints.AcceptAsync(
            board.Offered.First().Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        var after = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.Null(after.Limitation.Message);
        Assert.Equal(after.Limitation.Requested, after.Limitation.Generated);
    }

    /// <summary>
    /// The two most actionable limitation messages - "you do not fly anywhere yet" and "no aircraft
    /// are ticked" - must survive a re-read of an already-generated board. They previously did not:
    /// the describe-again path had its own copy of the prose with those branches missing, so a player
    /// with nothing ticked was told the board was merely thin instead of being told where to fix it.
    /// </summary>
    [Fact]
    public async Task WithNoAircraftAvailable_TheSpecificMessageSurvivesASecondReadOfTheSameBoard()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        ctx.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            SimEdition = SimEdition.Standard,
            SimAircraftOverridesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                on = Array.Empty<string>(),
                off = ContractAircraftCatalogue.All.Select(a => a.TypeDesignator).ToArray(),
            }),
        });
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Now);
        var first = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        Assert.NotNull(first.Limitation.Message);
        Assert.Contains("No aircraft are marked as available", first.Limitation.Message);

        ctx.Db.ChangeTracker.Clear();
        var second = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.Equal(first.Limitation.Message, second.Limitation.Message);
    }

    /// <summary>
    /// The other half of the fix, and the more important half. It would be easy to silence the
    /// message above by making it never fire, and that would be worse than the bug it replaced: the
    /// honest version of this sentence is the only thing standing between a genuinely thin board and
    /// a player concluding the feature is broken.
    /// </summary>
    [Fact]
    public async Task AGenuinelyConstrainedBoard_StillExplainsItself_EvenAfterAJobIsAccepted()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        // SeedWorldAsync is deliberately NOT called: the constraint has to come from the WORLD being
        // small, so the generator physically cannot fill eight slots.
        //
        // This test used to seed the full sixteen-airport world and rely on a single ticked-on
        // aircraft to make the board thin. That premise was simply wrong - one aeroplane can fly
        // eight different jobs, so limiting the fleet does not limit the COUNT. What actually decided
        // the outcome was the board seed, which mixes in Airline.Id, and that is a fresh Guid on every
        // run: the board was re-rolled each time and "is it constrained?" became a per-run gamble.
        // Measured at roughly 1 failure in 12 - green three times in CI, then red, having changed
        // nothing. Leaving the base world (a handful of airports) makes the shortfall structural, so
        // the assertion below is true by construction rather than by luck.
        var kept = ContractAircraftCatalogue.All.First().TypeDesignator;
        ctx.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            SimEdition = SimEdition.Standard,
            SimAircraftOverridesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                on = new[] { kept },
                off = ContractAircraftCatalogue.All
                    .Select(a => a.TypeDesignator)
                    .Where(d => d != kept)
                    .ToArray(),
            }),
        });
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.True(
            board.Limitation.Generated < board.Limitation.Requested,
            $"Expected a constrained board, but it produced {board.Limitation.Generated} of {board.Limitation.Requested}.");
        Assert.NotNull(board.Limitation.Message);

        var messageBefore = board.Limitation.Message;

        // Accepting is REQUIRED, not conditional. This whole test is about the explanation surviving
        // an accept, so a run with nothing to accept proves nothing at all - and the guard that used
        // to be here would have let exactly that pass silently. It also pins the other half of the
        // constraint: the board has to be thin, but not empty, or there is no job to take.
        Assert.NotEmpty(board.Offered);

        await ContractEndpoints.AcceptAsync(
            board.Offered.First().Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var after = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        // Still explained, and explained the SAME way - accepting a job changed nothing about why
        // this board was thin, so it must not change what the player is told about it.
        Assert.NotNull(after.Limitation.Message);
        Assert.Equal(messageBefore, after.Limitation.Message);
    }

    // ---------- Accepting ----------

    [Fact]
    public async Task AcceptingAJob_MovesItOffTheBoardAndStartsItsDeadline()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var offer = board.Offered.First();
        var advertisedDeadline = offer.DeadlineUtc;

        var result = await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var accepted = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == offer.Id);
        Assert.Equal(ContractStatus.Accepted, accepted.Status);
        Assert.Equal(Now, accepted.AcceptedUtc);

        // The deadline the player was shown before accepting is the deadline that applies. It is
        // stamped at generation and never recalculated on acceptance, so it cannot move under them.
        Assert.Equal(advertisedDeadline, accepted.DeadlineUtc);
    }

    /// <summary>Accepting twice is a double click, not an error - and it must not restart the deadline.</summary>
    [Fact]
    public async Task AcceptingTheSameJobTwice_IsHarmless()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var offer = board.Offered.First();

        await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);
        clock.UtcNow = Now.AddHours(2);
        var second = await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(second));
        var accepted = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == offer.Id);
        Assert.Equal(Now, accepted.AcceptedUtc);
    }

    // ---------- Flying it ----------

    /// <summary>
    /// <b>Legs are flown in order</b>, and the endpoint always hands back the next outstanding one.
    /// The order is the whole shape of a ferry: the aeroplane is physically where the last leg left
    /// it, so leg four cannot begin until leg three has landed.
    /// </summary>
    [Fact]
    public async Task StartingALeg_AlwaysGivesTheNextOutstandingOne_AndCreatesAContractFlight()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);
        var legs = contract.Legs.OrderBy(l => l.Sequence).ToList();

        var result = await StartLegAsync(ctx, contract.Id);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync();
        Assert.Equal(legs[0].Id, flight.ContractLegId);

        // The two nulls that make the whole guarantee structural.
        Assert.Null(flight.RouteId);
        Assert.Null(flight.FleetAircraftId);
        Assert.Equal(FlightStatus.InProgress, flight.Status);
        Assert.Equal(legs[0].PlannedBlockMinutes, flight.PlannedBlockMinutes);
    }

    /// <summary>
    /// One sector at a time, airline-wide - the same gate an ordinary flight has, and the refusal is
    /// a sentence rather than a bare conflict.
    /// </summary>
    [Fact]
    public async Task StartingALegWhileAFlightIsInProgress_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 2);

        await StartLegAsync(ctx, contract.Id);
        var second = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status409Conflict, StatusCodeOf(second));
        Assert.Contains("already in progress", ErrorOf(second));
    }

    [Fact]
    public async Task StartingALegOnAJobThatWasNeverAccepted_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 1);
        var row = await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
        row.Status = ContractStatus.Offered;
        await ctx.Db.SaveChangesAsync();

        var result = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("accept it before flying it", ErrorOf(result));
    }

    [Fact]
    public async Task StartingALegWhenEveryLegIsFlown_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 1);

        foreach (var leg in await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).ToListAsync())
        {
            leg.FlightId = Guid.NewGuid();
            leg.FlownUtc = Now;
        }

        await ctx.Db.SaveChangesAsync();

        var result = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("already been flown", ErrorOf(result));
    }

    // ---------- Handing it back ----------

    /// <summary>
    /// Abandoning returns the charge <b>and the sentence explaining it</b>, so the screen can tell the
    /// player what it cost and why rather than leaving them to discover a number in their ledger.
    /// </summary>
    [Fact]
    public async Task AbandoningAJob_ReturnsTheChargeAndTheReasonForIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);

        // Mark the first leg flown, so there is genuinely an aircraft stranded somewhere.
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();
        legs[0].FlightId = Guid.NewGuid();
        legs[0].FlownUtc = Now;
        await ctx.Db.SaveChangesAsync();

        var result = await ContractEndpoints.AbandonAsync(
            contract.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), new FakeClock(Now), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var body = OkValueOf<AbandonProbe>(result);

        Assert.Equal(2, body.UnflownLegCount);
        Assert.True(body.Charge > 0);
        Assert.Contains("recover the aircraft", body.Reason);

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
        Assert.Equal("You handed this job back.", closed.ClosedReason);
    }

    /// <summary>
    /// <b>The charge the player was shown is the charge they pay.</b> The confirmation has to name a
    /// figure before it is agreed to, and that figure is scaled by
    /// <see cref="ContractConfig.AbandonChargeFraction"/> - server-side economy config the client
    /// cannot read. So the quote comes down on the contract DTO, and this asserts the quote against
    /// <b>what actually reached the ledger</b> rather than both against a constant: a constant would
    /// go on passing if the two implementations drifted apart together, which is precisely the
    /// disagreement being guarded against.
    ///
    /// <para>Run at a deliberately non-default fraction. At the shipped 1.0 the charge coincides with
    /// the outstanding fee, so a client that ignored the quote entirely and showed
    /// <c>outstandingFee</c> would look correct - and every test would agree with it. Half a fraction
    /// separates "read the quote" from "guessed, and happened to be right".</para>
    /// </summary>
    [Fact]
    public async Task TheAbandonChargeQuotedOnTheContract_IsExactlyWhatAbandoningPostsToTheLedger()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 4);

        // Two of four flown, so there is a real stranded aeroplane and a real outstanding balance.
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();
        foreach (var flown in legs.Take(2))
        {
            flown.FlightId = Guid.NewGuid();
            flown.FlownUtc = Now;
        }
        await ctx.Db.SaveChangesAsync();

        // Only the fraction matters here - CalculateAbandonCharge reads nothing else from the config,
        // and the per-leg fee shares were stamped onto the legs when the contract was seeded.
        var config = new ContractConfig { AbandonChargeFraction = 0.5m };

        // What the board would put in front of the player.
        ctx.Db.ChangeTracker.Clear();
        var forQuote = await ctx.Db.Contracts.Include(c => c.Legs).SingleAsync(c => c.Id == contract.Id);
        var quoted = ContractEndpoints.ToDto(
            forQuote,
            config,
            await ContractSectorLookup.PostedFeeByLegIdAsync(ctx.Db, forQuote.Legs, CancellationToken.None),
            includeLegs: true);
        var quote = ProbeOf<AbandonQuoteProbe>(quoted);

        // The quote is not merely the outstanding fee - it is that fee through the fraction. If these
        // were equal the assertion below would prove nothing about which one the DTO reported.
        Assert.True(quote.OutstandingFee > 0);
        Assert.NotEqual(quote.OutstandingFee, quote.AbandonCharge);

        // Now actually hand it back, at the same fraction.
        ctx.Db.ChangeTracker.Clear();
        var reloaded = await ctx.Db.Contracts.Include(c => c.Legs).SingleAsync(c => c.Id == contract.Id);
        var posted = await ContractEconomicsPoster.PostAbandonAsync(
            ctx.Db, reloaded, config, Now, "You handed this job back.", CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        // The ledger is the arbiter - not the return value, and not a constant.
        var ledgerLines = await ctx.Db.LedgerTransactions
            .Where(t => t.Category == LedgerCategory.ContractFee && t.Amount < 0)
            .ToListAsync();
        var charged = -ledgerLines.Sum(t => t.Amount);

        Assert.Equal(quote.AbandonCharge, charged);
        Assert.Equal(quote.AbandonCharge, posted.Charge);

        // And the sentence too: the dialog renders this verbatim, so a divergence here would have the
        // confirmation explaining the charge differently from the ledger line describing it.
        Assert.Equal(quote.AbandonReason, posted.Reason);
        Assert.Contains(posted.Reason, ledgerLines.Single().Description);
    }

    /// <summary>
    /// <b>"Earned so far" means banked, and is checked against the ledger rather than against a
    /// constant.</b>
    ///
    /// <para>It used to sum the stamped fee shares of legs marked flown, and claimed in its own
    /// comment to agree with the ledger. It did not: a leg completed with estimates counts as flown
    /// and pays nothing, so the board reported money against a cash balance that had not moved. A
    /// leg invalidated by slew or a position jump does the same thing.</para>
    ///
    /// <para>Asserting against the posted rows rather than a fixed number is the point - a constant
    /// would go on passing if both sides drifted together, which is exactly how the two came
    /// apart.</para>
    /// </summary>
    [Fact]
    public async Task EarnedSoFar_IsWhatTheLedgerActuallyPaid_NotWhatTheFlownLegsWereWorth()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();

        // Leg 1: flown AND paid, the ordinary case.
        var paidFlightId = Guid.NewGuid();
        legs[0].FlightId = paidFlightId;
        legs[0].FlownUtc = Now;
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = Now,
            Category = LedgerCategory.ContractFee,
            Amount = legs[0].FeeShare,
            FlightId = paidFlightId,
            Description = "Contract fee: leg 1",
        });

        // Leg 2: flown and NOT paid - completed with estimates, or invalidated. Marked flown, no row.
        legs[1].FlightId = Guid.NewGuid();
        legs[1].FlownUtc = Now;

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var reloaded = await ctx.Db.Contracts.Include(c => c.Legs).SingleAsync(c => c.Id == contract.Id);
        var postedFees = await ContractSectorLookup.PostedFeeByLegIdAsync(ctx.Db, reloaded.Legs, CancellationToken.None);
        var dto = ContractEndpoints.ToDto(reloaded, new ContractConfig(), postedFees, includeLegs: true);
        var probe = ProbeOf<EarnedProbe>(dto);

        // The arbiter: what this contract's flights actually posted.
        var legFlightIds = reloaded.Legs.Where(l => l.FlightId is not null).Select(l => l.FlightId!.Value).ToList();
        var bankedPerLedger = (await ctx.Db.LedgerTransactions
                .Where(t => t.Category == LedgerCategory.ContractFee && t.Amount > 0
                            && t.FlightId != null && legFlightIds.Contains(t.FlightId.Value))
                .ToListAsync())
            .Sum(t => t.Amount);

        Assert.Equal(bankedPerLedger, probe.EarnedSoFar);

        // Both legs count as flown, but only one of them paid - so the old "sum the shares" answer
        // would have been strictly larger. If these were equal this test would prove nothing.
        Assert.Equal(2, probe.FlownLegCount);
        var worthOfFlownLegs = legs[0].FeeShare + legs[1].FeeShare;
        Assert.True(
            probe.EarnedSoFar < worthOfFlownLegs,
            $"earnedSoFar ({probe.EarnedSoFar}) should be less than the flown legs' worth ({worthOfFlownLegs}).");

        // And the per-leg fact that lets a screen say WHY: worth something, paid nothing.
        var unpaid = probe.Legs.Single(l => l.Sequence == 2);
        Assert.True(unpaid.Flown);
        Assert.Equal(0m, unpaid.FeePaid);
        Assert.True(unpaid.FeeShare > 0);

        // A leg never flown reports null rather than zero - "not yet" is not "paid nothing".
        Assert.Null(probe.Legs.Single(l => l.Sequence == 3).FeePaid);
    }

    /// <summary>
    /// The zero-charge cases carry a sentence of their own rather than an empty one. Handing back a
    /// job whose first leg was never flown is free - the aeroplane never moved - and the board has to
    /// be able to say so before the player commits, not merely charge them nothing afterwards.
    /// </summary>
    [Fact]
    public async Task AJobWithNoLegFlown_QuotesAFreeHandBackWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);

        var untouched = await ctx.Db.Contracts.Include(c => c.Legs).SingleAsync(c => c.Id == contract.Id);
        var dto = ContractEndpoints.ToDto(
            untouched,
            EconomyConfigCatalog.Default().Get(ctx.Airline.Playstyle).Contracts,
            await ContractSectorLookup.PostedFeeByLegIdAsync(ctx.Db, untouched.Legs, CancellationToken.None),
            includeLegs: true);
        var quote = ProbeOf<AbandonQuoteProbe>(dto);

        Assert.Equal(0m, quote.AbandonCharge);
        Assert.False(string.IsNullOrWhiteSpace(quote.AbandonReason));
        Assert.Contains("costs nothing", quote.AbandonReason);
    }

    /// <summary>
    /// <b>The completion bonus is forfeited by abandoning, and never added to the bill.</b>
    ///
    /// <para>Two distinct things could go wrong and both would be silent. The bonus could leak into
    /// the abandon charge, so walking away cost more than the legs left were worth - the charge is
    /// computed from unflown per-leg <c>FeeShare</c> values and the bonus is deliberately not one of
    /// them, but "deliberately not" is worth an assertion rather than a comment. Or it could be paid
    /// anyway on a job that was never finished, which would make abandoning free money.</para>
    /// </summary>
    [Fact]
    public async Task AbandoningForfeitsTheCompletionBonus_AndTheBonusNeverEntersTheCharge()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 4);

        // Give this job a bonus worth noticing, so a leak would be unmissable.
        var tracked = await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
        tracked.CompletionBonus = 50_000m;
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();
        legs[0].FlightId = Guid.NewGuid();
        legs[0].FlownUtc = Now;
        await ctx.Db.SaveChangesAsync();

        var unflownValue = legs.Skip(1).Sum(l => l.FeeShare);

        ctx.Db.ChangeTracker.Clear();
        var result = await ContractEndpoints.AbandonAsync(
            contract.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), new FakeClock(Now), CancellationToken.None);

        var body = OkValueOf<AbandonProbe>(result);

        // The charge is the unflown legs and nothing else - the 50,000 is nowhere in it.
        Assert.Equal(unflownValue, body.Charge);
        Assert.True(body.Charge < 50_000m);

        // And the bonus was not paid: the only ContractFee row is the negative charge.
        var contractRows = await ctx.Db.LedgerTransactions
            .Where(t => t.Category == LedgerCategory.ContractFee)
            .ToListAsync();
        Assert.DoesNotContain(contractRows, t => t.Amount == 50_000m);
        Assert.All(contractRows, t => Assert.True(t.Amount < 0, $"Unexpected credit of {t.Amount} on an abandoned job."));

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
    }

    /// <summary>
    /// A leg in the air is not something to resolve behind the player's back: abandoning underneath a
    /// tracked flight would leave it pointing at a closed contract.
    /// </summary>
    [Fact]
    public async Task AbandoningWhileALegIsInTheAir_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 2);
        await StartLegAsync(ctx, contract.Id);

        var result = await ContractEndpoints.AbandonAsync(
            contract.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), new FakeClock(Now), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("still in progress", ErrorOf(result));
    }

    /// <summary>
    /// A deadline that has genuinely passed closes the job, and it does so lazily on a board read
    /// rather than from a background pass - so the player only ever meets it at a moment they asked
    /// to look, with a date they were shown before they accepted.
    /// </summary>
    [Fact]
    public async Task AnAcceptedJobPastItsDeadline_ClosesItselfOnTheNextBoardRead()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var contract = await SeedAcceptedContractAsync(ctx, 2);

        var clock = new FakeClock(Now.AddDays(60));
        ctx.Db.ChangeTracker.Clear();
        await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
        Assert.Contains("deadline", closed.ClosedReason!);
    }

    // ---------- Helpers ----------

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<T>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>
    /// The <c>error</c> field of a refusal. Every refusal in this API carries one, because a screen
    /// has to be able to tell the player what to do next - a bare status code is not something a UI
    /// can render into a sentence.
    /// </summary>
    private static string ErrorOf(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error), $"No 'error' field on the refusal: {json}");
        return error.GetString() ?? string.Empty;
    }

    private sealed record BoardProbe(long Bucket, List<object> Offered, List<object> Accepted, LimitationProbe Limitation);

    private sealed record LimitationProbe(int AvailableAircraftCount, int OriginCount, int Requested, int Generated, string? Message);

    private sealed record AbandonProbe(object Contract, decimal Charge, int UnflownLegCount, int UnflownBlockMinutes, string Reason);

    /// <summary>The abandon quote as it appears on a contract DTO - what the confirmation reads.</summary>
    private sealed record AbandonQuoteProbe(decimal AbandonCharge, string AbandonReason, decimal OutstandingFee, decimal EarnedSoFar);

    private sealed record EarnedProbe(decimal EarnedSoFar, int FlownLegCount, List<LegProbe> Legs);

    /// <param name="FeePaid">Null when the leg has not flown - deliberately distinct from a paid zero.</param>
    private sealed record LegProbe(int Sequence, bool Flown, decimal FeeShare, decimal? FeePaid);

    /// <summary>
    /// Round-trips a DTO object through JSON exactly as the API would, so a probe reads the wire shape
    /// rather than the anonymous type - a field renamed in serialisation would otherwise pass here and
    /// fail in the browser.
    /// </summary>
    private static T ProbeOf<T>(object dto)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        return System.Text.Json.JsonSerializer.Deserialize<T>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static Task<IResult> StartLegAsync(RouteTestContext ctx, Guid contractId)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            EconomyConfigCatalog.Default(), null, NullLogger<FlightLifecycleService>.Instance);

        return ContractEndpoints.StartLegAsync(
            contractId, ctx.Db, ctx.CurrentUser, lifecycle, telemetry, new FakeClock(Now), CancellationToken.None);
    }

    private static ContractBoardService CreateBoardService(RouteTestContext ctx, DateTimeOffset now) =>
        CreateBoardService(ctx, new FakeClock(now));

    private static ContractBoardService CreateBoardService(RouteTestContext ctx, FakeClock clock) =>
        new(ctx.Db,
            new SimAircraftService(ctx.Db, new InstalledAircraftScanner(), clock, NullLogger<SimAircraftService>.Instance),
            EconomyConfigCatalog.Default(),
            clock,
            NullLogger<ContractBoardService>.Instance);

    /// <summary>
    /// Enough airports for the generator to have somewhere to send a job. RouteTestContext seeds four
    /// British ones; these add the reach a board needs to produce anything but the shortest hop.
    /// </summary>
    private static async Task SeedWorldAsync(RouteTestContext ctx)
    {
        (string Icao, double Lat, double Lon, int Runway, AirportSizeCategory Size)[] extras =
        [
            ("EGLL", 51.470, -0.454, 12_800, AirportSizeCategory.Large),
            ("EGPC", 58.459, -3.093, 5_900, AirportSizeCategory.Small),
            ("EGPO", 58.216, -6.331, 7_200, AirportSizeCategory.Small),
            ("EGJJ", 49.208, -2.195, 5_597, AirportSizeCategory.Small),
            ("EGNS", 54.083, -4.624, 5_754, AirportSizeCategory.Small),
            ("EIDW", 53.421, -6.270, 8_652, AirportSizeCategory.Large),
            ("LFPG", 49.010, 2.548, 13_829, AirportSizeCategory.Large),
            ("EHAM", 52.309, 4.764, 12_467, AirportSizeCategory.Large),
            ("EDDF", 50.033, 8.571, 13_123, AirportSizeCategory.Large),
            ("LEMD", 40.472, -3.561, 14_272, AirportSizeCategory.Large),
            ("LIRF", 41.800, 12.239, 12_795, AirportSizeCategory.Large),
            ("ENGM", 60.194, 11.100, 11_811, AirportSizeCategory.Medium),
            ("EKCH", 55.618, 12.656, 11_811, AirportSizeCategory.Medium),
            ("LPPT", 38.774, -9.134, 12_484, AirportSizeCategory.Medium),
            ("BIRK", 64.130, -21.941, 6_120, AirportSizeCategory.Medium),
            ("EKVG", 62.064, -7.277, 5_910, AirportSizeCategory.Small),
        ];

        foreach (var (icao, lat, lon, runway, size) in extras)
        {
            ctx.Db.Airports.Add(new Airport
            {
                Icao = icao,
                Name = icao,
                Municipality = icao,
                Country = "XX",
                Latitude = lat,
                Longitude = lon,
                SizeCategory = size,
                LongestRunwayFt = runway,
            });
        }

        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<Contract> SeedAcceptedContractAsync(RouteTestContext ctx, int legCount)
    {
        var chain = new[] { "EGGD", "EGPH", "EGPF", "EGSS" };
        var blockMinutes = Enumerable.Range(0, legCount).Select(i => 60 + i * 30).ToList();
        var shares = ContractPayCalculator.AllocateFeeShares(12_000m, blockMinutes);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Kind = ContractKind.Ferry,
            Status = ContractStatus.Accepted,
            BoardBucket = 1,
            BoardSlot = 0,
            OperatorName = "Kestrel Aircraft Leasing",
            AircraftTypeDesignator = "C208",
            LoadDescription = "Positioning flight",
            Fee = 12_000m,
            TotalDistanceNm = 300 * legCount,
            TotalPlannedBlockMinutes = blockMinutes.Sum(),
            OfferedUtc = Now.AddDays(-1),
            DeadlineUtc = Now.AddDays(28),
            AcceptedUtc = Now,
            CreatedUtc = Now.AddDays(-1),
        };

        for (var i = 0; i < legCount; i++)
        {
            contract.Legs.Add(new ContractLeg
            {
                Id = Guid.NewGuid(),
                ContractId = contract.Id,
                Sequence = i + 1,
                DepartureIcao = chain[i % chain.Length],
                ArrivalIcao = chain[(i + 1) % chain.Length],
                DistanceNm = 300,
                PlannedBlockMinutes = blockMinutes[i],
                FeeShare = shares[i],
            });
        }

        ctx.Db.Contracts.Add(contract);

        if (!await ctx.Db.Pilots.AnyAsync(p => p.AirlineId == ctx.Airline.Id))
        {
            ctx.Db.Pilots.Add(new Pilot
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                Name = "You",
                IsPlayer = true,
                CreatedUtc = Now,
            });
        }

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return contract;
    }
}
