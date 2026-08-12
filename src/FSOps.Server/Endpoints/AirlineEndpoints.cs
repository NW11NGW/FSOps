using System.Text.RegularExpressions;
using FSOps.Core.Airlines;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Money;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

public static class AirlineEndpoints
{
    // The names allowed in strategyProfile request bodies, generated from the enum itself so a
    // new profile (like Balanced) only ever needs adding in one place - Enums.cs - rather than
    // also being remembered in every validation message.
    private static string StrategyProfileOptionsText => string.Join(", ", Enum.GetNames<AirlineStrategyProfile>());

    // Same pattern as strategyProfile above, generated from AirlinePlaystyle itself so a third
    // preset can never be added without every validation message (and every test that iterates
    // Enum.GetValues<AirlinePlaystyle>()) picking it up automatically.
    private static string PlaystyleOptionsText => string.Join(", ", Enum.GetNames<AirlinePlaystyle>());

    /// <summary>
    /// The sensible fallback when the player leaves the onboarding "your name" field blank -
    /// defaulted rather than blocking progress, since nothing about founding an airline should
    /// stall on a field the player did not want to fill in. Matches what
    /// every player's pilot was named before this field existed, so leaving it blank changes
    /// nothing for anyone who skips it.
    /// </summary>
    private const string DefaultPilotName = "Local Pilot";

    public static void MapAirlineEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/airline", CreateAsync);
        group.MapGet("/airline", GetAsync);
        group.MapPut("/airline", UpdateAsync);
        group.MapGet("/airline/summary", GetSummaryAsync);
        group.MapGet("/airline/reputation", GetReputationAsync);
        group.MapGet("/airline/strategy-profiles", GetStrategyProfiles);
        group.MapGet("/airline/playstyles", GetPlaystyles);
        group.MapGet("/airline/ledger", GetLedgerAsync);
        group.MapDelete("/airline", DeleteAsync);
    }

    // internal, not private, so tests can drive it directly against an isolated RouteTestContext -
    // same convention as GetSummaryAsync below and FleetEndpoints.TakeLoanAsync/BuyAsync, which the
    // new starting-loan cap tests (tests/FSOps.Server.Tests) follow exactly.
    internal static async Task<IResult> CreateAsync(
        CreateAirlineRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 2 or > 40)
        {
            return Results.BadRequest(new { error = "Airline name must be between 2 and 40 characters." });
        }

        var icaoCode = (request.IcaoCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(icaoCode, "^[A-Z]{2,3}$"))
        {
            return Results.BadRequest(new { error = "ICAO code must be 2-3 letters." });
        }

        if (await db.Airlines.AnyAsync(a => a.IcaoCode == icaoCode, ct))
        {
            return Results.BadRequest(new { error = $"An airline with ICAO code '{icaoCode}' already exists." });
        }

        if (await db.Airlines.AnyAsync(a => a.OwnerUserId == currentUser.UserId, ct))
        {
            return Results.Conflict(new { error = "You already have an airline. Delete it first to start over." });
        }

        var homeAirportIcao = (request.HomeAirportIcao ?? string.Empty).Trim().ToUpperInvariant();
        var homeAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == homeAirportIcao, ct);
        if (homeAirport is null)
        {
            return Results.BadRequest(new { error = $"Home airport '{homeAirportIcao}' was not found." });
        }

        if (!homeAirport.HasScheduledService && homeAirport.LongestRunwayFt < 5000)
        {
            return Results.BadRequest(new
            {
                error = $"{homeAirportIcao} is too small to base an airline at - it needs scheduled service or a runway of at least 5,000 ft.",
            });
        }

        if (!Enum.TryParse<AirlineStrategyProfile>(request.StrategyProfile, ignoreCase: true, out var strategyProfile))
        {
            return Results.BadRequest(new { error = $"strategyProfile must be one of: {StrategyProfileOptionsText}." });
        }

        // Chosen once, here, and permanent for the airline's life. Casual -> True-life would
        // multiply an existing airline's fixed costs roughly twelvefold overnight and bankrupt a
        // healthy airline instantly; the reverse would trivialise everything already earned. Either
        // way its whole history would have been earned under rules that no longer apply, which
        // makes its numbers meaningless. There is deliberately no update path for this later (see
        // UpdateAsync, which never accepts it).
        if (!Enum.TryParse<AirlinePlaystyle>(request.Playstyle, ignoreCase: true, out var playstyle))
        {
            return Results.BadRequest(new { error = $"playstyle must be one of: {PlaystyleOptionsText}." });
        }

        var economyConfig = economyConfigCatalog.Get(playstyle);

        var accentColour = (request.AccentColour ?? string.Empty).Trim();
        if (!Regex.IsMatch(accentColour, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$"))
        {
            return Results.BadRequest(new { error = "accentColour must be a valid hex colour, e.g. #3b82f6." });
        }

        if (!Enum.TryParse<StarterAircraftFamily>(request.StarterAircraftFamily, ignoreCase: true, out var starterFamily))
        {
            return Results.BadRequest(new { error = "starterAircraftFamily must be one of: A320, B737." });
        }

        var currency = CurrencyCatalogue.TryGet(request.CurrencyCode);
        if (currency is null)
        {
            return Results.BadRequest(new { error = $"Unsupported currency code '{request.CurrencyCode}'." });
        }

        var starterIcaoType = starterFamily == StarterAircraftFamily.A320 ? "A320" : "B738";
        var familyName = starterFamily == StarterAircraftFamily.A320 ? "A320" : "B737";
        var aircraftType = await db.AircraftTypes.FirstOrDefaultAsync(t => t.IcaoType == starterIcaoType, ct)
            ?? await db.AircraftTypes.FirstOrDefaultAsync(t => t.Family == familyName, ct);
        if (aircraftType is null)
        {
            return Results.Problem("No starter aircraft type is available yet - world data may still be seeding.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        Loan? loan = null;
        if (request.StartingLoan is { } loanRequest)
        {
            if (loanRequest.Amount <= 0)
            {
                return Results.BadRequest(new { error = "startingLoan.amount must be greater than zero." });
            }

            if (loanRequest.TermMonths is < 1 or > 360)
            {
                return Results.BadRequest(new { error = "startingLoan.termMonths must be between 1 and 360." });
            }

            // A starting loan can never be bounded by LoanEligibilityCalculator's cash-flow-based
            // cap the way a mid-game loan (FleetEndpoints.TakeLoanAsync) is - a brand-new airline's
            // trailing cash flow is always exactly 0, and MaxMonthlyPayment(0) is always 0, so that
            // check would reject every starting loan of any size rather than cap it. Instead this
            // playstyle's own flat, hand-authored ceiling applies - see LoanConfig.MaxStartingLoanPrincipal's
            // own doc. Refused outright, never silently clamped: a player who asks for more than the
            // cap must be told so and given both figures, exactly like TakeLoanAsync's eligibility
            // refusal below, not quietly handed a smaller loan than they requested.
            if (loanRequest.Amount > economyConfig.Loan.MaxStartingLoanPrincipal)
            {
                return Results.BadRequest(new
                {
                    error = $"A starting loan of {loanRequest.Amount:F2} exceeds the maximum {economyConfig.Loan.MaxStartingLoanPrincipal:F2} " +
                             $"allowed for a new {playstyle} airline. Try a smaller amount, or grow your airline first and borrow again " +
                             "once it has a trading history.",
                    requestedAmount = loanRequest.Amount,
                    maxStartingLoanPrincipal = economyConfig.Loan.MaxStartingLoanPrincipal,
                });
            }

            // The rate is ALWAYS computed, never accepted from the request: a rate the player
            // supplies can be set to zero, which makes borrowing free and turns a loan from a
            // trade-off into an exploit. See LoanRateCalculator's own
            // doc. StartingLoanRequest deliberately has no annualRatePct field at all, so there is
            // nothing here to validate or ignore; a caller that still sends one (an old client, or a
            // deliberate probe) is simply talking to a schema that no longer has that property, and
            // System.Text.Json drops unknown properties rather than erroring.
            //
            // A brand-new airline has no trading history yet - there is no ledger to compute a
            // trailing cash flow from, because the airline this loan belongs to doesn't exist until
            // later in this same method. That is passed through as exactly 0m (not looked up), which
            // LoanRateCalculator's doc explains resolves to the playstyle's cap rate - the correct,
            // maximum-risk-premium price for an unproven borrower.
            var startingLoanRate = LoanRateCalculator.ComputeAnnualRatePct(
                loanRequest.Amount, loanRequest.TermMonths, trailing30DayNetOperatingCashFlow: 0m, economyConfig.Loan);

            loan = new Loan
            {
                Id = Guid.NewGuid(),
                Principal = loanRequest.Amount,
                AnnualInterestRate = startingLoanRate,
                TermMonths = loanRequest.TermMonths,
                MonthlyPayment = LoanCalculator.MonthlyPayment(loanRequest.Amount, startingLoanRate, loanRequest.TermMonths),
                RemainingBalance = loanRequest.Amount,
                StartUtc = DateTimeOffset.UtcNow,
                IsPaidOff = false,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
        }

        var now = DateTimeOffset.UtcNow;
        var airline = new Airline
        {
            Id = Guid.NewGuid(),
            Name = name,
            IcaoCode = icaoCode,
            HomeAirportIcao = homeAirportIcao,
            StrategyProfile = strategyProfile,
            Playstyle = playstyle,
            AccentColour = accentColour,
            ReputationScore = 50,
            OwnerUserId = currentUser.UserId,
            CreatedUtc = now,
        };

        var registration = AircraftRegistrationGenerator.Generate(homeAirport.Country);
        var fleetAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            AircraftTypeId = aircraftType.Id,
            Registration = registration,
            // A new airline leases its starter aircraft rather than buying outright, which is how
            // a real start-up begins and what keeps starting capital lean. Buying becomes a
            // mid-game milestone funded by retained profit or a deliberate loan, not the opening move.
            Ownership = AircraftOwnership.Leased,
            AirframeHours = 0,
            HoursSinceACheck = 0,
            HoursSinceCCheck = 0,
            ConditionPercent = 100,
            LocationIcao = homeAirportIcao,
            Status = FleetAircraftStatus.Active,
            // A brand-new airline has exactly one aircraft, so it defaults to reserved-for-player -
            // the one-aircraft case is handled deliberately rather than by the general rule. If
            // the reserve-one-for-the-player rule applied unconditionally, virtual pilots could
            // never fly at all for a new airline. So the player chooses explicitly whether a pilot
            // may fly their only aircraft, and
            // defaulting to protected is the safe choice for that explicit-choice requirement
            // (releasing it is one click away via PUT /fleet/{id}/reservation, forcing it to fly
            // for a pilot silently would not be). See FleetAircraft.ReservedForPlayer's own doc for
            // how this invariant is maintained as the fleet grows.
            ReservedForPlayer = true,
            CreatedUtc = now,
        };

        // The founding lease uses the playstyle's own lease rate for this type, not
        // AircraftType.MonthlyLeaseRate - that catalogue column is shared across every airline
        // regardless of playstyle and regardless of when its database was seeded, so it can't hold
        // two different figures for the same aircraft type at once (see EconomyConfig.LeaseRates'
        // doc comment). Everything from here on (the deposit below, and every monthly lease payment
        // EconomyClockService posts) reads from this Lease row, so the playstyle distinction is
        // captured once, at creation, and never needs re-deriving. Same resolution FleetEndpoints
        // uses for every later lease, so a second A320 is never cheaper than the first just because
        // it came from a different endpoint.
        var starterLeaseRate = economyConfig.LeaseRateFor(aircraftType.IcaoType);

        var lease = new Lease
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            MonthlyRate = starterLeaseRate,
            StartUtc = now,
            IsActive = true,
            CreatedUtc = now,
        };

        // The player names their own pilot at onboarding - it is their own identity in their own
        // airline, appearing on every flight they fly and throughout the Pilots page.
        // currentUser.DisplayName is a fixed placeholder ("Local Pilot") from the
        // single-user LocalUser stub, never a real identity. Trimmed and defaulted sensibly rather
        // than blocking founding an airline over a blank field; the same default LocalUser used to
        // supply unconditionally, so blank input changes nothing for a player who skips it.
        var pilotName = (request.PilotName ?? string.Empty).Trim();
        if (pilotName.Length == 0)
        {
            pilotName = DefaultPilotName;
        }
        else if (pilotName.Length > 40)
        {
            return Results.BadRequest(new { error = "pilotName must be 40 characters or fewer." });
        }

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Name = pilotName,
            IsPlayer = true,
            MonthlySalary = economyConfig.AirlineStartup.StartingPilotMonthlySalary,
            HoursFlown = 0,
            SkillRating = 50,
            Status = PilotStatus.Available,
            CreatedUtc = now,
        };

        // The deposit scales with the playstyle's own starter lease rate (not
        // aircraftType.MonthlyLeaseRate - see starterLeaseRate's own comment above) so it stays
        // sensible whichever starter family the player picks and whichever playstyle they chose.
        var leaseDeposit = Math.Round(starterLeaseRate * (decimal)economyConfig.AirlineStartup.LeaseDepositMonths, 2, MidpointRounding.AwayFromZero);

        var ledgerEntries = new List<LedgerTransaction>
        {
            new()
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.StartingCapital, Amount = economyConfig.AirlineStartup.StartingCapital,
                Description = "Starting capital",
            },
            new()
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.LeasePayment, Amount = -leaseDeposit,
                Description = $"Lease deposit ({economyConfig.AirlineStartup.LeaseDepositMonths:0.#} months): {aircraftType.Name} ({registration})",
            },
        };

        if (loan is not null)
        {
            loan.AirlineId = airline.Id;
            ledgerEntries.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.LoanProceeds, Amount = loan.Principal,
                Description = "Loan proceeds",
            });
        }

        db.Airlines.Add(airline);
        db.FleetAircraft.Add(fleetAircraft);
        db.Leases.Add(lease);
        db.Pilots.Add(pilot);
        db.LedgerTransactions.AddRange(ledgerEntries);
        if (loan is not null)
        {
            db.Loans.Add(loan);
        }

        await UpsertCurrencyAsync(db, currentUser.UserId, currency.Code, ct);

        // A single SaveChangesAsync call commits every add above in one SQLite transaction,
        // so the airline, fleet, pilot, ledger rows and settings either all land or none do.
        await db.SaveChangesAsync(ct);

        return Results.Created("/api/v1/airline", await BuildSummaryAsync(db, airline, ct));
    }

    private static async Task UpsertCurrencyAsync(FsOpsDbContext db, Guid ownerUserId, string currencyCode, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        if (settings is null)
        {
            db.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ownerUserId, CurrencyCode = currencyCode });
        }
        else
        {
            settings.CurrencyCode = currencyCode;
        }
    }

    private static async Task<IResult> GetAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        return airline is null ? Results.NoContent() : Results.Ok(airline);
    }

    private static async Task<IResult> UpdateAsync(UpdateAirlineRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound(new { error = "You don't have an airline yet." });
        }

        // The ICAO code identifies the airline in flight records and the home base is an
        // economic anchor - neither can be changed after creation. A caller echoing back the
        // airline's current values (e.g. spreading the existing object into the request) is
        // fine; only an actual attempted change is rejected.
        if (request.IcaoCode is not null && !string.Equals(request.IcaoCode.Trim(), airline.IcaoCode, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "icaoCode cannot be changed after your airline is created." });
        }

        if (request.HomeAirportIcao is not null && !string.Equals(request.HomeAirportIcao.Trim(), airline.HomeAirportIcao, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "homeAirportIcao cannot be changed after your airline is created." });
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is < 2 or > 40)
            {
                return Results.BadRequest(new { error = "Airline name must be between 2 and 40 characters." });
            }

            airline.Name = name;
        }

        if (request.AccentColour is not null)
        {
            var accentColour = request.AccentColour.Trim();
            if (!Regex.IsMatch(accentColour, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$"))
            {
                return Results.BadRequest(new { error = "accentColour must be a valid hex colour, e.g. #3b82f6." });
            }

            airline.AccentColour = accentColour;
        }

        if (request.StrategyProfile is not null)
        {
            if (!Enum.TryParse<AirlineStrategyProfile>(request.StrategyProfile, ignoreCase: true, out var strategyProfile))
            {
                return Results.BadRequest(new { error = $"strategyProfile must be one of: {StrategyProfileOptionsText}." });
            }

            // Going forward only: this changes future suggested fares and demand, and the
            // advisories a new route preview may raise. Completed flights, posted ledger lines
            // and fares already stored on existing routes are historical fact and are never
            // touched - LedgerTransaction and FlightEvent are append-only, and existing Route
            // rows keep whatever fare they were created with.
            airline.StrategyProfile = strategyProfile;
        }

        if (request.PilotName is not null)
        {
            var pilotName = request.PilotName.Trim();
            if (pilotName.Length is < 1 or > 40)
            {
                return Results.BadRequest(new { error = "pilotName must be between 1 and 40 characters." });
            }

            var playerPilot = await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id && p.IsPlayer, ct);
            if (playerPilot is not null)
            {
                playerPilot.Name = pilotName;
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(airline);
    }

    /// <summary>
    /// Per-strategy metadata for the profile picker (onboarding and Settings -&gt; Airline), sourced
    /// from the same economy-config.json the pricing/demand engine reads and the same advisory
    /// rules RoutePreviewCalculator actually applies - so the UI can never describe a strategy
    /// differently to how it behaves. No airline lookup: this is static reference data, needed
    /// before an airline exists (onboarding) as much as after (settings). Derived from
    /// Enum.GetValues so a sixth profile appears automatically rather than needing this endpoint
    /// remembered too.
    /// </summary>
    private static IResult GetStrategyProfiles(EconomyConfigCatalog economyConfigCatalog)
    {
        // Strategy-profile figures (fare/elasticity/load-factor/cost multipliers) live in the
        // shared base of economy-config.json, not either playstyle's override block - see
        // EconomyConfigCatalog's class doc - so they are identical whichever playstyle resolves
        // them. Casual is picked here purely as a concrete instance to read them from; there is no
        // airline in scope yet for this endpoint (it backs onboarding as much as Settings).
        var economyConfig = economyConfigCatalog.Get(AirlinePlaystyle.Casual);
        var profiles = Enum.GetValues<AirlineStrategyProfile>()
            .Select(profile =>
            {
                var strategy = economyConfig.GetStrategy(profile);
                var rules = RoutePreviewCalculator.AdvisoryRulesFor(profile);
                return new StrategyProfileInfo(
                    profile.ToString(),
                    strategy.ReferenceFareMultiplier,
                    strategy.Elasticity,
                    strategy.BaselineLoadFactor,
                    strategy.CostMultiplier,
                    rules.WarnsOnInternationalSector,
                    rules.WarnsOnShortDomesticHop);
            })
            .ToList();

        return Results.Ok(profiles);
    }

    /// <summary>
    /// Per-playstyle metadata for the onboarding picker and the read-only Settings display, sourced
    /// straight from economy-config.json's resolved "casual"/"trueLife" configs so the UI can never
    /// quote a figure that has drifted from what airline creation actually charges. Derived from
    /// Enum.GetValues so a third playstyle would need to be added here to appear at all - the same
    /// self-documenting pattern as <see cref="GetStrategyProfiles"/>. No airline lookup: needed
    /// before an airline exists (onboarding) as much as after (Settings, where it is shown but
    /// never editable).
    /// </summary>
    private static IResult GetPlaystyles(EconomyConfigCatalog economyConfigCatalog)
    {
        var playstyles = Enum.GetValues<AirlinePlaystyle>()
            .Select(playstyle =>
            {
                var config = economyConfigCatalog.Get(playstyle);
                return new PlaystyleInfo(
                    playstyle.ToString(),
                    Description: playstyle switch
                    {
                        AirlinePlaystyle.Casual =>
                            "Forgiving fixed costs so one leg a day already runs a growing airline. A missed flight is skipped quietly and maintenance downtime is compressed - a nuisance, not a fortnight off. The honest choice for playing in short, occasional sessions.",
                        AirlinePlaystyle.TrueLife =>
                            "Real-world lease, insurance and deposit figures - a single aircraft flown casually runs at a real loss, so the airline genuinely depends on hiring virtual pilots to fly standing schedules. A missed flight is cancelled at a real cost, and maintenance grounds an aircraft for realistic stretches (a C-check is about a fortnight). The honest choice if you want to run something closer to an actual carrier.",
                        _ => throw new InvalidOperationException($"No description written for playstyle '{playstyle}'."),
                    },
                    Immutable: true,
                    ImmutableReason: "Chosen once at creation and permanent for the life of this airline - changing it later would either bankrupt a healthy airline or trivialise everything already earned. Switching means deleting the airline and starting a new one.",
                    config.AirlineStartup.StartingCapital,
                    config.AirlineStartup.LeaseDepositMonths,
                    // "A320"/"B738" - the exact ICAO types CreateAsync's starterIcaoType resolves
                    // the A320/B737 starter families to (see its own mapping above), so this always
                    // quotes the same rate the founding lease would actually charge.
                    config.LeaseRateFor("A320"),
                    config.LeaseRateFor("B738"),
                    config.FleetFinance.MonthlyInsurancePerAircraft,
                    config.Loan.CapAnnualRatePct);
            })
            .ToList();

        return Results.Ok(playstyles);
    }

    internal static async Task<IResult> GetSummaryAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        return airline is null ? Results.NoContent() : Results.Ok(await BuildSummaryAsync(db, airline, ct));
    }

    private static async Task<object> BuildSummaryAsync(FsOpsDbContext db, Airline airline, CancellationToken ct)
    {
        // Cash balance is never a stored column - it's always the sum of the append-only ledger,
        // so it can never drift out of sync with how it got there. SQLite has no native decimal
        // type, and EF's Sqlite provider can't translate Sum() over decimal into SQL, so the
        // (small, per-airline) set of amounts is pulled into memory and summed there instead.
        var amounts = await db.LedgerTransactions
            .Where(t => t.AirlineId == airline.Id)
            .Select(t => t.Amount)
            .ToListAsync(ct);
        var cashBalance = amounts.Sum();
        var fleetCount = await db.FleetAircraft.CountAsync(f => f.AirlineId == airline.Id, ct);

        // Routes are always a there-and-back pair (see RouteEndpoints.CreateAsync): each direction
        // is stored as its own row so it can carry its own flight number, but from the owner's
        // point of view EGGD<->EGPH is *one* route, not two. Counting raw rows would double-count
        // every pair, so this counts distinct unordered airport pairs instead - a leg that hasn't
        // been paired yet (e.g. momentarily, before ListAsync's self-heal runs) still counts as
        // one route rather than zero.
        var legDirections = await db.Routes
            .Where(r => r.AirlineId == airline.Id)
            .Select(r => new { r.DepartureIcao, r.ArrivalIcao })
            .ToListAsync(ct);
        var routeCount = legDirections
            .Select(d => string.CompareOrdinal(d.DepartureIcao, d.ArrivalIcao) <= 0
                ? (d.DepartureIcao, d.ArrivalIcao)
                : (d.ArrivalIcao, d.DepartureIcao))
            .Distinct()
            .Count();

        var pilotCount = await db.Pilots.CountAsync(p => p.AirlineId == airline.Id, ct);

        // So Settings -> Airline can prefill the "your name" field without a separate /pilots
        // fetch. Falls back to the same default CreateAsync uses if, somehow, the player pilot
        // record is missing (never expected, but this must never throw over it).
        var playerPilotName = await db.Pilots
            .Where(p => p.AirlineId == airline.Id && p.IsPlayer)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? DefaultPilotName;

        return new { airline, cashBalance, fleetCount, routeCount, pilotCount, playerPilotName };
    }

    /// <summary>
    /// The window of most recent Completed/Cancelled/Skipped sectors this endpoint bases the
    /// reputation summary on. Reputation is tuned to be "noticeable but slow" - 40-60 consistent
    /// sectors to climb from 50 to 75 - so a single sector should never swing the reported
    /// "direction". This therefore looks at a
    /// recent handful rather than either one flight or the airline's entire history.
    /// </summary>
    private const int ReputationWindowSectors = 15;

    /// <summary>A recent-average target has to clear this many points above/below the current score
    /// before the UI calls it "improving"/"declining" rather than "steady" - keeps the label from
    /// flipping on a single borderline sector.</summary>
    private const double ReputationDirectionThreshold = 2.0;

    /// <summary>
    /// The dashboard's reputation card. A number that silently governs demand and never appears in
    /// the UI is indistinguishable from a bug, which is the whole reason this endpoint exists.
    /// <see cref="Airline.ReputationScore"/> is already a plain field
    /// on <see cref="GetAsync"/>/<see cref="GetSummaryAsync"/>; this endpoint answers the two things
    /// a bare number can't show - which way it is moving, and what is moving it. Both are derived
    /// fresh from the append-only <see cref="Flight"/> rows on every call (point 4: "the safest
    /// shape is to recompute... from those rows rather than incrementing a counter on the side") -
    /// nothing here is stored, so it can never drift from what actually happened.
    /// </summary>
    internal static async Task<IResult> GetReputationAsync(
        FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var demandMultiplier = DemandCalculator.ReputationDemandMultiplier(economyConfig.Demand, airline.ReputationScore);

        // SQLite can't translate OrderBy over DateTimeOffset (same trap as GetLedgerAsync above) -
        // materialise every candidate sector first, then order and window in memory. Suspended and
        // every not-yet-resolved status are deliberately excluded: a maintenance suspension is never
        // reputation's business (see ReputationCalculator's own doc) and Planned/InProgress/
        // Interrupted/Abandoned never posted a reputation outcome at all.
        var candidates = await db.Flights
            .Where(f => f.AirlineId == airline.Id && f.DeletedUtc == null &&
                (f.Status == FlightStatus.Completed || f.Status == FlightStatus.Cancelled || f.Status == FlightStatus.Skipped))
            .ToListAsync(ct);

        var recent = candidates
            .OrderByDescending(f => f.InUtc ?? f.PlannedDepartureUtc)
            .Take(ReputationWindowSectors)
            .ToList();

        // A brand-new airline (or one with nothing recent) has no honest trend to report: it sits
        // at exactly 50 with no history behind it, and that state has to read as deliberate rather
        // than as a broken reading. "new" is a distinct value from "steady" so the UI can say so plainly rather
        // than implying a flat trend it has no evidence for.
        if (recent.Count == 0)
        {
            return Results.Ok(new ReputationSummary(
                airline.ReputationScore, "new", 0, null, 0, 0, null, Math.Round(demandMultiplier, 3)));
        }

        var cancelledCount = recent.Count(f => f.Status == FlightStatus.Cancelled);
        var skippedCount = recent.Count(f => f.Status == FlightStatus.Skipped);

        // On-time is only measurable for a completed sector with a real arrival time and no
        // elevated-sim-rate flag - the exact exclusion FlightLifecycleService itself applies before
        // ever calling ReputationPoster, see Flight.SimRateElevated's own doc.
        var measurableDelays = recent
            .Where(f => f.Status == FlightStatus.Completed && f.InUtc is not null && !f.SimRateElevated)
            .Select(f => (f.InUtc!.Value - f.PlannedDepartureUtc.AddMinutes(f.PlannedBlockMinutes)).TotalMinutes)
            .ToList();
        var onTimePercent = measurableDelays.Count == 0
            ? (double?)null
            : Math.Round(100.0 * measurableDelays.Count(d => d <= economyConfig.Reputation.OnTimeToleranceMinutes) / measurableDelays.Count, 1);

        var landingFpms = recent
            .Where(f => f.Status == FlightStatus.Completed && f.LandingFpmFirst is not null)
            .Select(f => f.LandingFpmFirst!.Value)
            .ToList();
        string? landingQuality = null;
        if (landingFpms.Count > 0)
        {
            var avgFpm = landingFpms.Average();
            var band = (economyConfig.Reputation.LandingWorstFpm - economyConfig.Reputation.LandingBestFpm) / 3.0;
            landingQuality = avgFpm <= economyConfig.Reputation.LandingBestFpm + band
                ? "smooth"
                : avgFpm >= economyConfig.Reputation.LandingWorstFpm - band
                    ? "hard"
                    : "firm";
        }

        // Direction of travel: the same per-sector "target" Advance actually steps the score toward
        // (see ReputationCalculator.TargetForCompletedFlight), averaged across the window. Advance
        // always moves currentScore toward target, so a recent average ABOVE the current score means
        // the score has been (and will keep) trending up toward it; below means trending down.
        // ReputationDirectionThreshold keeps a single near-borderline sector from flipping the label.
        var targets = recent
            .Select(f => f.Status == FlightStatus.Completed
                ? ReputationCalculator.TargetForCompletedFlight(
                    economyConfig.Reputation,
                    f.SimRateElevated || f.InUtc is null ? null : (f.InUtc.Value - f.PlannedDepartureUtc.AddMinutes(f.PlannedBlockMinutes)).TotalMinutes,
                    f.LandingFpmFirst)
                : economyConfig.Reputation.CancelledTargetScore)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .ToList();

        string direction;
        if (targets.Count == 0)
        {
            // Every candidate in the window had no measurable signal at all (e.g. only manually
            // completed flights with no telemetry) - nothing honest to call a trend, so this reads
            // as "steady" rather than guessing a direction from no evidence.
            direction = "steady";
        }
        else
        {
            var avgTarget = targets.Average();
            direction = avgTarget - airline.ReputationScore > ReputationDirectionThreshold
                ? "improving"
                : airline.ReputationScore - avgTarget > ReputationDirectionThreshold
                    ? "declining"
                    : "steady";
        }

        return Results.Ok(new ReputationSummary(
            airline.ReputationScore, direction, recent.Count, onTimePercent, cancelledCount, skippedCount,
            landingQuality, Math.Round(demandMultiplier, 3)));
    }

    /// <summary>
    /// The itemised ledger, newest first - so the player can see exactly what they were charged and
    /// why, including the monthly lease/salary/insurance lines <see cref="FSOps.Server.Services.EconomyClockService"/>
    /// posts. This is the plain backend view and nothing in the UI consumes it: the Finances page
    /// has its own richer, category-filterable <c>GET /finance/ledger</c> instead.
    /// <c>limit</c> caps the page size (default 100, max 1000); <c>cashBalance</c> and
    /// <c>totalCount</c> are computed from the whole ledger, not just the returned page, so the UI
    /// can show "showing 100 of 4,213" honestly. SQLite can't translate OrderBy over
    /// DateTimeOffset, so the ledger is materialised first and ordered in memory - same rule as
    /// FlightLifecycleService.RehydrateInProgressFlightAsync.
    /// </summary>
    private static async Task<IResult> GetLedgerAsync(int? limit, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var take = Math.Clamp(limit ?? 100, 1, 1000);

        var transactions = await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).ToListAsync(ct);
        var cashBalance = transactions.Sum(t => t.Amount);
        var ordered = transactions.OrderByDescending(t => t.Utc).Take(take).ToList();

        return Results.Ok(new { cashBalance, totalCount = transactions.Count, transactions = ordered });
    }

    private static async Task<IResult> DeleteAsync(bool? confirm, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (confirm != true)
        {
            return Results.BadRequest(new { error = "Pass ?confirm=true to delete your airline. This cannot be undone." });
        }

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var now = DateTimeOffset.UtcNow;
        airline.DeletedUtc = now;

        // Cascade the soft-delete to the airline's own fleet/pilots/routes so "start over"
        // leaves a genuinely clean slate rather than orphaned-but-still-visible rows. The
        // ledger itself is left alone - it's an append-only historical record, harmless once
        // its airline is gone.
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var aircraft in fleet)
        {
            aircraft.DeletedUtc = now;
        }

        var pilots = await db.Pilots.Where(p => p.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var pilot in pilots)
        {
            pilot.DeletedUtc = now;
        }

        // Schedules cascade with their pilots - see PilotEndpoints.ReleaseAsync for the same
        // cascade on a single pilot's release. Without this, VirtualFlightResolverService would
        // keep quietly resolving occurrences for a pilot whose airline no longer exists.
        var schedules = await db.PilotSchedules.Where(s => s.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var schedule in schedules)
        {
            schedule.DeletedUtc = now;
        }

        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var entries = await db.PilotScheduleEntries.Where(e => scheduleIds.Contains(e.PilotScheduleId)).ToListAsync(ct);
        foreach (var entry in entries)
        {
            entry.DeletedUtc = now;
        }

        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var route in routes)
        {
            route.DeletedUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public record CreateAirlineRequest(
    string? Name,
    string? IcaoCode,
    string? HomeAirportIcao,
    string? StrategyProfile,
    string? Playstyle,
    string? AccentColour,
    string? StarterAircraftFamily,
    string? CurrencyCode,
    StartingLoanRequest? StartingLoan,
    /// <summary>
    /// The player's own name for their founding pilot - it appears on every flight they fly and
    /// throughout the Pilots page, so it should be theirs rather than a placeholder.
    /// Optional: blank or omitted falls back to <see cref="AirlineEndpoints.DefaultPilotName"/>
    /// rather than blocking airline creation.
    /// </summary>
    string? PilotName = null);

/// <summary>
/// A loan taken at the same moment the airline is created. Deliberately has NO rate field: a rate
/// the player supplies can be set to zero, which makes borrowing free. The rate is always
/// computed by <see cref="FSOps.Core.Finance.LoanRateCalculator"/>; there is nothing here for a
/// caller to supply or for the endpoint to trust.
/// </summary>
public record StartingLoanRequest(decimal Amount, int TermMonths);

/// <summary>
/// GET /airline/reputation - see AirlineEndpoints.GetReputationAsync for how each field is derived.
/// <see cref="Direction"/> is "improving" | "declining" | "steady" | "new" (no recent sectors at
/// all - a brand-new airline). <see cref="OnTimePercent"/> and <see cref="LandingQuality"/>
/// ("smooth" | "firm" | "hard") are null when nothing in the window could measure that signal
/// honestly, never zero. <see cref="DemandMultiplier"/> is the same figure DemandCalculator applies
/// to every route right now (1.0 = baseline at reputation 50).
/// </summary>
public record ReputationSummary(
    double Score,
    string Direction,
    int SectorsConsidered,
    double? OnTimePercent,
    int CancelledCount,
    int SkippedCount,
    string? LandingQuality,
    double DemandMultiplier);

public record UpdateAirlineRequest(
    string? Name,
    string? AccentColour,
    string? StrategyProfile,
    string? IcaoCode,
    string? HomeAirportIcao,
    /// <summary>
    /// Renames the player's own pilot from Settings, since the name chosen at onboarding should not
    /// be permanent.
    /// Null means "leave unchanged" (same convention as every other field here); an explicit blank
    /// string is rejected rather than silently reset, since a deliberate edit blanking the field is
    /// a mistake to catch, not the same "leave it blank" case onboarding allows.
    /// </summary>
    string? PilotName = null);

/// <summary>
/// The figures behind one strategy profile, straight from economy-config.json plus the route
/// advisories RoutePreviewCalculator actually raises for it - everything the profile picker needs
/// to describe the profile honestly. ReferenceFareMultiplier/CostMultiplier are relative to 1.0 =
/// baseline; BaselineLoadFactor is a fraction (0.73 = 73%); Elasticity is demand's sensitivity to
/// fare (higher = more sensitive, seats empty faster as fare rises above the reference).
/// </summary>
public record StrategyProfileInfo(
    string Profile,
    decimal ReferenceFareMultiplier,
    double Elasticity,
    double BaselineLoadFactor,
    double CostMultiplier,
    bool WarnsOnInternationalSector,
    bool WarnsOnShortDomesticHop);

/// <summary>
/// The figures and honest description behind one playstyle, for the onboarding picker and the
/// read-only Settings display: the consequences of the choice have to be legible before it is
/// made, because it is locked for the airline's life. The two
/// starter lease rates are shown separately (rather than a single figure) because the founding
/// lease depends on which starter aircraft family the player picks alongside this.
/// <para>
/// <see cref="StartingLoanAnnualRatePct"/> is included so the onboarding review step can show the
/// rate a startup loan will actually carry, never a player-editable field - taking a loan should
/// be an informed decision, not a surprise, and the rate is the simulation's to set. It always equals
/// <see cref="LoanConfig.CapAnnualRatePct"/>: a brand-new airline has no trading history, so
/// <see cref="LoanRateCalculator"/> always prices its starting loan at the playstyle's
/// ceiling (see that class's own doc) - this is simply that same, single source of truth, quoted
/// here rather than re-derived in the frontend.
/// </para>
/// </summary>
public record PlaystyleInfo(
    string Playstyle,
    string Description,
    bool Immutable,
    string ImmutableReason,
    decimal StartingCapital,
    double LeaseDepositMonths,
    decimal StarterLeaseRateA320,
    decimal StarterLeaseRateB737,
    decimal MonthlyInsurancePerAircraft,
    double StartingLoanAnnualRatePct);
