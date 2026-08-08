using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Services;

/// <summary>
/// The only place a flight's money becomes real, append-only <see cref="LedgerTransaction"/> rows.
/// Every integrity rule in docs/PLAN.md "Integrity" is enforced structurally here rather than
/// re-implemented by each caller: a slew/position-jump flight posts no ticket revenue because the
/// code path that would add it is never reached, not because a computed figure gets zeroed out
/// afterwards.
/// <para>
/// <b>Fuel is charged on uplift, not on burn, and never here.</b> <see cref="PostFuelUplift"/> can
/// be called any number of times over a flight's life - at start (reconciling whatever changed
/// while FSOps wasn't watching, see <c>FlightEndpoints.StartAsync</c>), and any time a live-tracked
/// flight shows a real rise in fuel while on the ground (see
/// <c>FlightLifecycleService.ProcessSample</c>) - once per genuine uplift event, each posting its
/// own ledger line naming the airport it happened at. <see cref="FleetAircraft.FuelOnBoardKg"/> is
/// the persisted asset this charges against: burning fuel already owned costs nothing further, so
/// a return leg flown on fuel already in the tank posts no fuel line at all. A decrease in fuel
/// while on the ground (defuelling) is deliberately a non-event, not a credit - see
/// <see cref="FSOps.Core.Flights.GroundFuelChangeKind"/> - so nothing here handles that direction.
/// </para>
/// </summary>
public static class FlightEconomicsPoster
{
    // EconomyState (the world seed the fuel-price random walk keys off) has no seeder yet -
    // nothing in the app creates a row, so this falls back to a fixed seed to keep fuel pricing
    // at least deterministic per (airport, day) until EconomyClockService and its seeding land.
    private const int FallbackWorldSeed = 1;

    public static async Task<int> ResolveWorldSeedAsync(FsOpsDbContext db, CancellationToken ct)
    {
        var state = await db.EconomyStates.FirstOrDefaultAsync(ct);
        return state?.WorldSeed ?? FallbackWorldSeed;
    }

    /// <summary>
    /// Charges for fuel at the airport it's bought at - never on burn. Posted unconditionally:
    /// fuel bought is fuel bought, whether or not the flight it was bought for ever completes (see
    /// the abandoned-flight rule - abandoning does not un-buy it). Returns the amount charged (0
    /// if nothing was uplifted) so the caller can fold it into <see cref="Flight.TotalCost"/>.
    /// <paramref name="upliftAirport"/> is wherever the aircraft actually was when the rise was
    /// observed - the departure airport for a normal pre-flight fill-up, but potentially the
    /// arrival airport (or, for a diversion, wherever it diverted to) for a turnaround uplift
    /// detected live while still tracked.
    /// </summary>
    public static decimal PostFuelUplift(
        FsOpsDbContext db,
        Flight flight,
        EconomyConfig config,
        Airport upliftAirport,
        double upliftKg,
        DateTimeOffset utc,
        int worldSeed)
    {
        if (upliftKg <= 0)
        {
            return 0m;
        }

        var pricePerKg = FuelPricing.PricePerKg(config.Fuel, upliftAirport.Icao, upliftAirport.Country, utc, worldSeed);
        var cost = FlightCostCalculator.FuelUpliftCost(upliftKg, pricePerKg);
        if (cost <= 0)
        {
            return 0m;
        }

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = flight.AirlineId,
            Utc = utc,
            Category = LedgerCategory.Fuel,
            Amount = -cost,
            FlightId = flight.Id,
            Description = $"Fuel uplift at {upliftAirport.Icao}: {upliftKg:F0} kg @ {pricePerKg:F4}/kg",
        });

        return cost;
    }

    /// <summary>
    /// Posts every non-fuel line for a completed sector (fuel was already charged at uplift - see
    /// <see cref="PostFuelUpliftAsync"/>). Idempotent on <see cref="Flight.RevenuePosted"/>: once
    /// true, a second call is a no-op, so a retry, reconnect, or crash rehydration can never post
    /// twice. Returns the computed result (null if nothing new was posted, either because this
    /// flight was already processed or because the sector isn't payable) purely for the caller's
    /// own logging/DTO purposes - the ledger rows this writes are the only figures that matter.
    /// </summary>
    public static async Task<FlightEconomicsResult?> PostCompletionAsync(
        FsOpsDbContext db,
        Flight flight,
        Airline airline,
        Route route,
        AircraftType aircraftType,
        Airport arrivalAirport,
        EconomyConfig config,
        double flightHours,
        DateTimeOffset utc,
        CancellationToken ct)
    {
        if (flight.RevenuePosted)
        {
            return null;
        }

        // Structural gate: a slew/position-jump flight never reaches the code below that would
        // compute or post ticket revenue - see docs/PLAN.md "Integrity". Fuel, charged separately
        // at uplift, is the only cost that stays posted for a sector like this.
        var payable = !(flight.SlewDetected || flight.PositionJumpDetected);
        if (!payable)
        {
            flight.RevenuePosted = true;
            return null;
        }

        var departureAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        // Small is the safe/conservative fallback if the departure airport somehow can't be found -
        // only feeds catchment for the demand model here, never a fee (those are all keyed off
        // the arrival airport's own size, resolved separately above).
        var departureSize = departureAirport?.SizeCategory ?? AirportSizeCategory.Small;

        var referenceFare = ReferenceFareCalculator.Calculate(config, airline.StrategyProfile, route.DistanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            config.Demand, departureSize, arrivalAirport.SizeCategory, route.DistanceNm, utc, airline.ReputationScore);

        var result = FlightEconomicsCalculator.Calculate(
            config,
            airline.StrategyProfile,
            route.BaseFare,
            referenceFare,
            aircraftType.PaxCapacity,
            marketDemandPax,
            upliftKg: 0,
            pricePerKgAtUpliftAirport: 0m,
            arrivalAirport.SizeCategory,
            aircraftType.MtowTonnes,
            flightHours);

        flight.PaxBooked = result.PaxBooked;
        flight.PaxFlown = result.PaxBooked;
        flight.Revenue = result.TicketRevenue;
        flight.TotalCost += result.TotalCost; // TotalCost already carries the fuel line posted at start; result.FuelCost is 0 here (upliftKg: 0 above), so this never double-counts it.

        Post(db, flight, LedgerCategory.TicketRevenue, result.TicketRevenue, utc, $"Ticket revenue: {result.PaxBooked} pax x {route.BaseFare:F2}");
        Post(db, flight, LedgerCategory.LandingFees, -result.LandingFee, utc, $"Landing fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Handling, -result.HandlingFee, utc, $"Handling fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Handling, -result.ParkingFee, utc, $"Parking fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Handling, -result.PassengerCharge, utc, $"Passenger charges at {arrivalAirport.Icao} ({result.PaxBooked} pax)");
        Post(db, flight, LedgerCategory.Handling, -result.TurnaroundFee, utc, $"Turnaround/gate fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Maintenance, -result.MaintenanceAccrual, utc, "Maintenance accrual");
        Post(db, flight, LedgerCategory.Salary, -result.CrewCost, utc, "Crew cost (this sector)");

        flight.RevenuePosted = true;
        return result;
    }

    private static void Post(FsOpsDbContext db, Flight flight, LedgerCategory category, decimal amount, DateTimeOffset utc, string description)
    {
        if (amount == 0m)
        {
            return;
        }

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = flight.AirlineId,
            Utc = utc,
            Category = category,
            Amount = amount,
            FlightId = flight.Id,
            Description = description,
        });
    }
}
