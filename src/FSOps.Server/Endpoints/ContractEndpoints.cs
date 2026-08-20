using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Core.SimAircraft;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// The contract-flying API: browse the board, take a job, fly its legs in order, or hand it back.
///
/// <para>Shaped for a caller that has to <b>render a browsable board</b>, <b>show progress through a
/// multi-leg job</b>, and <b>explain a refusal</b>. Every refusal here comes back as a sentence a
/// player can act on rather than a status code - a job that cannot be started because the previous
/// leg is outstanding, or because the aircraft is not where the leg begins, is a normal thing to
/// happen and the screen has to be able to say which.</para>
/// </summary>
public static class ContractEndpoints
{
    public static void MapContractEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/contracts/board", BoardAsync);
        group.MapGet("/contracts/{id:guid}", GetAsync);
        group.MapPost("/contracts/{id:guid}/accept", AcceptAsync);
        group.MapPost("/contracts/{id:guid}/abandon", AbandonAsync);
        group.MapPost("/contracts/{id:guid}/start-leg", StartLegAsync);
    }

    /// <summary>
    /// The board: what is on offer this period, what the player has already accepted, when it next
    /// refreshes, and - when it is thinner than it should be - why.
    /// </summary>
    internal static async Task<IResult> BoardAsync(
        FsOpsDbContext db, ICurrentUser currentUser, ContractBoardService boardService,
        EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before browsing contracts." });
        }

        var state = await boardService.GetBoardAsync(airline, currentUser.UserId, ct);
        var config = economyConfigCatalog.Get(airline.Playstyle).Contracts;

        // One query for the whole board rather than one per contract. Offers have no flown legs, so
        // in practice this only ever resolves the accepted jobs' legs.
        var postedFees = await ContractSectorLookup.PostedFeeByLegIdAsync(
            db, state.Offered.Concat(state.Accepted).SelectMany(c => c.Legs), ct);

        return Results.Ok(new
        {
            bucket = state.Bucket,
            refreshesUtc = state.RefreshesUtc,
            offered = state.Offered.Select(c => ToDto(c, config, postedFees, includeLegs: true)).ToList(),
            accepted = state.Accepted.Select(c => ToDto(c, config, postedFees, includeLegs: true)).ToList(),
            limitation = new
            {
                availableAircraftCount = state.Limitation.AvailableAircraftCount,
                originCount = state.Limitation.OriginCount,
                requested = state.Limitation.Requested,
                generated = state.Limitation.Generated,
                // Null when the board came out full. Never an empty string: "there is nothing to say"
                // and "there is something to say and it is blank" must not look the same to a caller.
                message = state.Limitation.Message,
            },
        });
    }

    internal static async Task<IResult> GetAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog,
        CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var contract = await db.Contracts
            .Include(c => c.Legs)
            .FirstOrDefaultAsync(c => c.Id == id && c.AirlineId == airline.Id, ct);

        if (contract is null)
        {
            return Results.NotFound();
        }

        var postedFees = await ContractSectorLookup.PostedFeeByLegIdAsync(db, contract.Legs, ct);
        return Results.Ok(ToDto(
            contract, economyConfigCatalog.Get(airline.Playstyle).Contracts, postedFees, includeLegs: true));
    }

    /// <summary>
    /// Takes a job. The deadline shown on the offer is the deadline that applies - it is stamped at
    /// generation and never recalculated here, so what the player agreed to is exactly what they were
    /// shown.
    /// </summary>
    internal static async Task<IResult> AcceptAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog,
        IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before accepting contracts." });
        }

        var contract = await db.Contracts
            .Include(c => c.Legs)
            .FirstOrDefaultAsync(c => c.Id == id && c.AirlineId == airline.Id, ct);

        if (contract is null)
        {
            return Results.NotFound(new { error = "That contract could not be found." });
        }

        var config = economyConfigCatalog.Get(airline.Playstyle).Contracts;
        var postedFees = await ContractSectorLookup.PostedFeeByLegIdAsync(db, contract.Legs, ct);

        if (contract.Status == ContractStatus.Accepted)
        {
            // Not an error - a double click, or a stale board. Return the contract as it stands.
            return Results.Ok(ToDto(contract, config, postedFees, includeLegs: true));
        }

        if (contract.Status != ContractStatus.Offered)
        {
            return Results.BadRequest(new
            {
                error = contract.Status == ContractStatus.Expired
                    ? "That job is no longer on the board - it expired when the board refreshed."
                    : $"That job is {contract.Status.ToString().ToLowerInvariant()} and can't be accepted again.",
            });
        }

        var now = clock.UtcNow;
        if (contract.DeadlineUtc <= now)
        {
            return Results.BadRequest(new { error = "That job's deadline has already passed." });
        }

        contract.Status = ContractStatus.Accepted;
        contract.AcceptedUtc = now;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(contract, config, postedFees, includeLegs: true));
    }

    /// <summary>
    /// Hands an accepted job back. Charges for the legs not flown - see
    /// <see cref="ContractPayCalculator.CalculateAbandonCharge"/>, which is also where the reason
    /// this is not simply punitive lives. The response carries the charge and the sentence explaining
    /// it, so the screen can tell the player what it cost and why rather than showing a number that
    /// appeared in their ledger.
    /// </summary>
    internal static async Task<IResult> AbandonAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog,
        IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline first." });
        }

        var contract = await db.Contracts
            .Include(c => c.Legs)
            .FirstOrDefaultAsync(c => c.Id == id && c.AirlineId == airline.Id, ct);

        if (contract is null)
        {
            return Results.NotFound(new { error = "That contract could not be found." });
        }

        if (contract.Status != ContractStatus.Accepted)
        {
            return Results.BadRequest(new { error = $"That job is {contract.Status.ToString().ToLowerInvariant()}, not in progress." });
        }

        // A leg of this contract in the air is not a state to resolve behind the player's back: the
        // aircraft is somewhere between two airports and abandoning underneath it would leave a
        // tracked flight pointing at a closed contract.
        var legIds = contract.Legs.Select(l => l.Id).ToList();
        var inFlight = await db.Flights.AnyAsync(
            f => f.ContractLegId != null && legIds.Contains(f.ContractLegId.Value) &&
                 (f.Status == FlightStatus.InProgress || f.Status == FlightStatus.Interrupted), ct);

        if (inFlight)
        {
            return Results.BadRequest(new
            {
                error = "A leg of this contract is still in progress. Finish or abandon that flight before handing the job back.",
            });
        }

        var config = economyConfigCatalog.Get(airline.Playstyle).Contracts;
        var charge = await ContractEconomicsPoster.PostAbandonAsync(
            db, contract, config, clock.UtcNow, "You handed this job back.", ct);

        await db.SaveChangesAsync(ct);

        // Read AFTER the abandon is saved, so the returned contract reflects the world as it now is.
        // The abandon charge itself carries no FlightId and so cannot appear in this sum - what the
        // job paid and what walking away from it cost stay two separate figures.
        var postedFees = await ContractSectorLookup.PostedFeeByLegIdAsync(db, contract.Legs, ct);

        return Results.Ok(new
        {
            contract = ToDto(contract, config, postedFees, includeLegs: true),
            charge = charge.Charge,
            unflownLegCount = charge.UnflownLegCount,
            unflownBlockMinutes = charge.UnflownBlockMinutes,
            reason = charge.Reason,
        });
    }

    /// <summary>
    /// Starts the next outstanding leg of an accepted contract as a real, tracked flight.
    ///
    /// <para>The aeroplane belongs to the operator, so there is no fleet aircraft to reserve, no
    /// position to check against the player's own fleet and no range refusal to make - the leg was
    /// range-checked against this aircraft when the contract was generated, which is the promise the
    /// whole feature rests on. What IS checked is that the player is not already flying something,
    /// and that legs are flown in order.</para>
    /// </summary>
    internal static async Task<IResult> StartLegAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle,
        SimTelemetryService telemetry, IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before flying contracts." });
        }

        var contract = await db.Contracts
            .Include(c => c.Legs)
            .FirstOrDefaultAsync(c => c.Id == id && c.AirlineId == airline.Id, ct);

        if (contract is null)
        {
            return Results.NotFound(new { error = "That contract could not be found." });
        }

        if (contract.Status != ContractStatus.Accepted)
        {
            return Results.BadRequest(new { error = $"That job is {contract.Status.ToString().ToLowerInvariant()} - accept it before flying it." });
        }

        // The same airline-wide gate an ordinary flight has: one sector at a time.
        if (await db.Flights.AnyAsync(f => f.AirlineId == airline.Id && f.Status == FlightStatus.InProgress, ct))
        {
            return Results.Conflict(new { error = "A flight is already in progress. Complete or abandon it before starting another." });
        }

        // Legs are flown in order, and the order is the whole shape of a ferry: the aeroplane is
        // physically where the last leg left it, so leg four cannot begin until leg three has landed.
        var leg = contract.Legs.Where(l => l.FlightId is null).OrderBy(l => l.Sequence).FirstOrDefault();
        if (leg is null)
        {
            return Results.BadRequest(new { error = "Every leg of this contract has already been flown." });
        }

        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == leg.DepartureIcao, ct);
        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == leg.ArrivalIcao, ct);
        if (departure is null || arrival is null)
        {
            return Results.Problem("This leg's airports could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id && p.IsPlayer, ct)
            ?? await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.Problem("Your airline has no pilot on record.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var contractAircraft = ContractAircraftCatalogue.Find(contract.AircraftTypeDesignator);

        // Type matching stays informational and is NEVER penalised, exactly as it is for an airline
        // sector. The player may well load a different variant of the aeroplane the contract names,
        // and that is a note on the report card rather than a reason to refuse the leg or dock the
        // fee. Null means "the sim told us nothing to compare", which is a third state and not a
        // failed comparison.
        var currentAircraft = telemetry.CurrentAircraft;
        var titleFlown = currentAircraft?.Title ?? string.Empty;
        var atcModel = currentAircraft?.AtcModel;
        bool? typeMismatch = contractAircraft is not null && AircraftTypeMatcher.HasAircraftData(titleFlown, atcModel)
            ? !AircraftTypeMatcher.IsMatch(contractAircraft.MatchPatterns, titleFlown, atcModel)
            : null;

        var now = clock.UtcNow;
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            // No route and no fleet aircraft - this is somebody else's aeroplane. See Flight.RouteId
            // and FlightWriteInvariant.
            RouteId = null,
            FleetAircraftId = null,
            ContractLegId = leg.Id,
            PilotId = pilot.Id,
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = now,
            PlannedBlockMinutes = leg.PlannedBlockMinutes,
            // A contract's passengers are the operator's, not the airline's. Recorded so the report
            // card can show what was carried; never priced, never counted as the airline's load
            // factor (see StatsEndpoints.BuildDailyFlightMetrics).
            PaxBooked = contract.PaxCount,
            FuelPlannedKg = 0,
            TitleFlown = titleFlown,
            TypeMismatch = typeMismatch,
            Revenue = 0m,
            TotalCost = 0m,
            CreatedUtc = now,
        };

        FlightWriteInvariant.Validate(flight);
        db.Flights.Add(flight);
        await db.SaveChangesAsync(ct);

        lifecycle.BeginTracking(
            flight.Id, airline.Id, fleetAircraftId: null, arrival.Icao, flight.PlannedBlockMinutes,
            (departure.Latitude, departure.Longitude), contractLegId: leg.Id);

        return Results.Created($"/api/v1/flights/{flight.Id}", new
        {
            flightId = flight.Id,
            contractId = contract.Id,
            contractLegId = leg.Id,
            legSequence = leg.Sequence,
            legCount = contract.Legs.Count,
            departureIcao = leg.DepartureIcao,
            arrivalIcao = leg.ArrivalIcao,
            distanceNm = Math.Round(leg.DistanceNm, 1),
            plannedBlockMinutes = leg.PlannedBlockMinutes,
            feeShare = leg.FeeShare,
            aircraftTypeDesignator = contract.AircraftTypeDesignator,
            aircraftName = contractAircraft?.Name,
            typeMismatch,
        });
    }

    /// <summary>
    /// One contract as the board renders it. Everything a player needs to decide is here, including
    /// the whole chain of stops - the user's own decision was that a contract LISTS its stops, so
    /// they always know exactly what they are accepting before they accept it.
    /// </summary>
    /// <param name="postedFeeByLegId">
    /// What each leg has actually been paid, from <see cref="ContractSectorLookup.PostedFeeByLegIdAsync"/>.
    /// Required rather than optional: <c>earnedSoFar</c> is a money figure and this app has one source
    /// of truth for money, which is the ledger.
    /// </param>
    internal static object ToDto(
        Contract contract, ContractConfig config, IReadOnlyDictionary<Guid, decimal> postedFeeByLegId, bool includeLegs)
    {
        var aircraft = ContractAircraftCatalogue.Find(contract.AircraftTypeDesignator);
        var legs = contract.Legs.OrderBy(l => l.Sequence).ToList();
        var flownLegs = legs.Count(l => l.FlightId is not null);

        // What handing this back would cost, quoted through the SAME call that posts it (see
        // ContractEconomicsPoster.QuoteAbandon). The confirmation has to name the charge before the
        // player agrees to it, and the fraction it is scaled by is server-side config the client
        // cannot read - so the figure has to come from here rather than be inferred there.
        var abandon = ContractEconomicsPoster.QuoteAbandon(legs, config);

        return new
        {
            id = contract.Id,
            kind = contract.Kind.ToString(),
            status = contract.Status.ToString(),
            operatorName = contract.OperatorName,

            aircraft = new
            {
                typeDesignator = contract.AircraftTypeDesignator,
                name = aircraft?.Name,
                manufacturer = aircraft?.Manufacturer,
                category = aircraft?.Category.ToString(),
                rangeNm = aircraft?.RangeNm,
                cruiseTasKts = aircraft?.CruiseTasKts,
                seats = aircraft?.Seats,
            },

            loadDescription = contract.LoadDescription,
            payloadKg = contract.PayloadKg,
            paxCount = contract.PaxCount,

            fee = contract.Fee,

            // Paid only on finishing every leg, and forfeited by handing the job back. On the board
            // BEFORE the player accepts, because a bonus nobody knows about cannot influence the
            // decision it exists to influence. Zero for a single-leg job.
            completionBonus = contract.CompletionBonus,
            // What the whole job is worth if it is seen through - the figure a player actually
            // compares between offers. Named separately rather than folded into `fee`, because `fee`
            // is what the legs sum to and the two must not be confused.
            totalIfCompleted = contract.Fee + contract.CompletionBonus,

            totalDistanceNm = Math.Round(contract.TotalDistanceNm, 1),
            totalPlannedBlockMinutes = contract.TotalPlannedBlockMinutes,

            legCount = legs.Count,
            flownLegCount = flownLegs,

            // What the player has actually BANKED - summed from the posted ledger rows, not from the
            // stamped shares of legs that happen to be marked flown.
            //
            // Those are different numbers, and the difference is not hypothetical: completing a leg
            // with estimates marks it flown and pays nothing, and so does a sector invalidated by slew
            // or a position jump. The old version summed the shares and claimed in its own comment to
            // agree with the ledger; it did not, and the board showed "Earned so far" figures against
            // a cash balance that had not moved. Money comes from the ledger in this app, always.
            earnedSoFar = legs.Where(l => l.FlightId is not null)
                .Sum(l => postedFeeByLegId.GetValueOrDefault(l.Id, 0m)),

            // Still the stamped shares, and correctly so - this is what the REMAINING legs are worth,
            // which is a fact about the future rather than a claim about money already moved.
            outstandingFee = legs.Where(l => l.FlightId is null).Sum(l => l.FeeShare),

            // Both straight from the calculator, so the dialog quotes exactly what the ledger will
            // show. Zero is a real answer and carries its own sentence: handing back a job whose first
            // leg was never flown costs nothing, and the reason says so.
            abandonCharge = abandon.Charge,
            abandonReason = abandon.Reason,

            offeredUtc = contract.OfferedUtc,
            deadlineUtc = contract.DeadlineUtc,
            acceptedUtc = contract.AcceptedUtc,
            closedUtc = contract.ClosedUtc,
            closedReason = contract.ClosedReason,

            // The next sector to fly, so a screen does not have to work out the ordering rule for
            // itself and then get it subtly different from the server's.
            nextLeg = legs.FirstOrDefault(l => l.FlightId is null) is { } next
                ? new { next.Id, next.Sequence, next.DepartureIcao, next.ArrivalIcao }
                : null,

            legs = includeLegs
                ? legs.Select(l => new
                {
                    id = l.Id,
                    sequence = l.Sequence,
                    departureIcao = l.DepartureIcao,
                    arrivalIcao = l.ArrivalIcao,
                    distanceNm = Math.Round(l.DistanceNm, 1),
                    plannedBlockMinutes = l.PlannedBlockMinutes,
                    // What this leg is WORTH, stamped at generation.
                    feeShare = l.FeeShare,
                    // What it actually PAID. Equal to feeShare for a leg flown in the simulator; zero
                    // for one completed with estimates or invalidated by slew or a position jump,
                    // both of which count as flown and pay nothing. Null when the leg has not been
                    // flown at all - which is a third state, not a payment of zero.
                    feePaid = l.FlightId is null ? (decimal?)null : postedFeeByLegId.GetValueOrDefault(l.Id, 0m),
                    flown = l.FlightId is not null,
                    flightId = l.FlightId,
                    flownUtc = l.FlownUtc,
                }).ToList<object>()
                : null,
        };
    }
}
