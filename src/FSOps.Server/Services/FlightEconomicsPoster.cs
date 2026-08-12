using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Services;

/// <summary>
/// The only place a flight's money becomes real, append-only <see cref="LedgerTransaction"/> rows.
/// Every integrity rule is enforced structurally here rather than
/// re-implemented by each caller: a slew/position-jump flight posts no ticket revenue because the
/// code path that would add it is never reached, not because a computed figure gets zeroed out
/// afterwards.
/// <para>
/// <b>Fuel is charged on what was burned, never on what was merely carried.</b> <see cref="PostFuelBurn"/>
/// posts once per sector, at whatever point the burn becomes known - a normal completion
/// (<c>FlightLifecycleService.FinalizeFlightAsync</c>), a manual completion or virtual-pilot
/// occurrence with no telemetry to measure from (both bill the sector's own planned figure), or an
/// abandoned flight (bills whatever was measurably burned before the abandon, or nothing at all if
/// that can't be determined - see <c>FlightEndpoints.AbandonAsync</c>). Posted regardless of
/// whether the sector turns out to be payable: fuel actually burned is a real cost either way, the
/// same way it always has been in this app.
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
    /// Charges for fuel at the departure airport's price - never anywhere else, since that is
    /// where the aircraft would realistically have been fuelled for this sector. Posted
    /// unconditionally with respect to whether the sector is payable: fuel actually burned is a
    /// real cost whether or not the flight it was burned for ever earned anything (see the
    /// slew/position-jump rule - an invalid sector still keeps its fuel cost). Returns the amount
    /// charged (0 if nothing was billable) so the caller can fold it into
    /// <see cref="Flight.TotalCost"/>.
    /// </summary>
    public static decimal PostFuelBurn(
        FsOpsDbContext db,
        Flight flight,
        EconomyConfig config,
        Airport departureAirport,
        double burnedKg,
        DateTimeOffset utc,
        int worldSeed)
    {
        if (burnedKg <= 0)
        {
            return 0m;
        }

        var pricePerKg = FuelPricing.PricePerKg(config.Fuel, departureAirport.Icao, departureAirport.Country, utc, worldSeed);
        var cost = FlightCostCalculator.FuelBurnCost(burnedKg, pricePerKg);
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
            Description = $"Fuel: {burnedKg:F0} kg burned, billed at {departureAirport.Icao} @ {pricePerKg:F4}/kg",
        });

        return cost;
    }

    /// <summary>
    /// Posts every non-fuel line for a completed sector (fuel is billed separately - see
    /// <see cref="PostFuelBurn"/>). Idempotent on <see cref="Flight.RevenuePosted"/>: once
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
        // compute or post ticket revenue: teleporting the aircraft is unambiguous, and a sector
        // flown that way is not paid for. Fuel, charged separately
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
        flight.TotalCost += result.TotalCost; // Fuel is posted separately via PostFuelBurn; result.FuelCost is 0 here (upliftKg: 0 above), so this never double-counts it.

        Post(db, flight, LedgerCategory.TicketRevenue, result.TicketRevenue, utc, $"Ticket revenue: {result.PaxBooked} pax x {route.BaseFare:F2}");
        Post(db, flight, LedgerCategory.LandingFees, -result.LandingFee, utc, $"Landing fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Handling, -result.HandlingFee, utc, $"Handling fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.ParkingFees, -result.ParkingFee, utc, $"Parking fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.PassengerCharges, -result.PassengerCharge, utc, $"Passenger charges at {arrivalAirport.Icao} ({result.PaxBooked} pax)");
        Post(db, flight, LedgerCategory.TurnaroundFees, -result.TurnaroundFee, utc, $"Turnaround/gate fee at {arrivalAirport.Icao}");
        Post(db, flight, LedgerCategory.Maintenance, -result.MaintenanceAccrual, utc, "Maintenance accrual");
        Post(db, flight, LedgerCategory.CrewCost, -result.CrewCost, utc, "Crew cost (this sector)");

        flight.RevenuePosted = true;
        return result;
    }

    /// <summary>
    /// Posts the modest VATSIM online-flying bonus (G12) - see
    /// <see cref="EconomyConfig.VatsimOnlineBonus"/>'s own doc. Callers (currently only
    /// <see cref="FlightLifecycleService.FinalizeFlightAsync"/>) must have already established that
    /// this flight qualifies - <see cref="Flight.VatsimOnline"/> true and
    /// <see cref="Flight.VatsimOnlineFraction"/> at or above
    /// <see cref="Core.Economy.VatsimOnlineBonusConfig.MinimumOnlineFraction"/> - and that
    /// <see cref="PostCompletionAsync"/> actually posted ticket revenue for this sector (a slew/
    /// position-jump flight, or one <see cref="PostCompletionAsync"/> otherwise declined to price,
    /// earns no bonus either - there is nothing to be "extra" on top of). Computed as a fraction of
    /// THIS sector's own <paramref name="ticketRevenue"/>, never a flat figure, so it scales with
    /// the sector actually flown rather than being a fixed reward regardless of size. Returns the
    /// amount posted (0 if the computed bonus rounds to nothing) purely for the caller's own
    /// bookkeeping (folding it into <see cref="Flight.Revenue"/>).
    /// </summary>
    public static decimal PostVatsimOnlineBonus(
        FsOpsDbContext db, Flight flight, EconomyConfig config, decimal ticketRevenue, DateTimeOffset utc)
    {
        var bonus = Math.Round(ticketRevenue * (decimal)config.VatsimOnlineBonus.RevenueUpliftFraction, 2, MidpointRounding.AwayFromZero);
        if (bonus <= 0)
        {
            return 0m;
        }

        var callsignSuffix = string.IsNullOrWhiteSpace(flight.VatsimCallsign) ? string.Empty : $" as {flight.VatsimCallsign}";
        Post(
            db, flight, LedgerCategory.VatsimOnlineBonus, bonus, utc,
            $"VATSIM online-flying bonus ({config.VatsimOnlineBonus.RevenueUpliftFraction:P0} uplift, flown{callsignSuffix})");

        return bonus;
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
