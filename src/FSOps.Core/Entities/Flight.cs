namespace FSOps.Core.Entities;

public class Flight
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public Guid RouteId { get; set; }

    /// <summary>
    /// For a virtual-pilot flight, the <see cref="PilotScheduleEntry"/> that produced this
    /// occurrence - null for a player flight (started from the Fly screen, which has no schedule
    /// entry at all). Deliberately not a foreign key with cascade behaviour: a completed flight is
    /// historical fact and must keep showing which schedule entry generated it even after the
    /// player edits or deletes that entry from the current week - see VirtualFlightResolverService.
    /// </summary>
    public Guid? ScheduleId { get; set; }

    public Guid FleetAircraftId { get; set; }

    public Guid PilotId { get; set; }

    public FlightStatus Status { get; set; } = FlightStatus.Planned;

    public DateTimeOffset PlannedDepartureUtc { get; set; }

    public int PlannedBlockMinutes { get; set; }

    // OOOI - Out/Off/On/In times, captured as the flight actually progresses.
    public DateTimeOffset? OutUtc { get; set; }

    public DateTimeOffset? OffUtc { get; set; }

    public DateTimeOffset? OnUtc { get; set; }

    public DateTimeOffset? InUtc { get; set; }

    public int PaxBooked { get; set; }

    public int PaxFlown { get; set; }

    public double FuelPlannedKg { get; set; }

    public double FuelUsedKg { get; set; }

    public double? LandingFpmFirst { get; set; }

    public double? LandingFpmHardest { get; set; }

    public double? LandingGForce { get; set; }

    public double? CentrelineDeviationM { get; set; }

    public string TitleFlown { get; set; } = string.Empty;

    /// <summary>
    /// Informational only - a wrong-family aircraft is flagged, never penalised. Three-valued: true
    /// is a genuine family mismatch, false is a confirmed match, and null means unknown - the sim
    /// reported no TITLE or ATC MODEL at all to check (not connected, or no aircraft loaded yet) -
    /// see <see cref="FSOps.Core.Flights.AircraftTypeMatcher.HasAircraftData"/>. Never collapse null
    /// into false or true: "we don't know" is a different fact from "we checked and it matched" or
    /// "we checked and it didn't".
    /// </summary>
    public bool? TypeMismatch { get; set; }

    /// <summary>
    /// True if the sim ran faster than real time (simulation rate above 1.0) at any point while
    /// this flight was tracked. Not a penalty - accelerating a long cruise is normal single-player
    /// behaviour - but it does mean anything measured in wall-clock time (block-time variance,
    /// on-time performance) is meaningless for this flight and must be reported as "not measured"
    /// rather than scored, and no reputation may be gained from it. Landing quality is unaffected -
    /// it comes from the sim's own instantaneous touchdown telemetry, not elapsed time - and is
    /// still scored normally. See FlightIntegrityMonitor.
    /// </summary>
    public bool SimRateElevated { get; set; }

    /// <summary>Highest simulation rate observed while this flight was tracked. 1.0 (normal speed) if <see cref="SimRateElevated"/> is false.</summary>
    public double MaxSimulationRateObserved { get; set; } = 1.0;

    /// <summary>
    /// True if slew mode was active at any point while this flight was tracked. Unlike
    /// <see cref="SimRateElevated"/>, this means the sector is not valid for payment - a
    /// structural gate, not a deduction: whatever posts revenue for this flight must check this
    /// (and <see cref="PositionJumpDetected"/>) rather than pay out a reduced amount.
    /// </summary>
    public bool SlewDetected { get; set; }

    /// <summary>
    /// True if two consecutive samples implied a physically impossible ground speed (see
    /// FlightIntegrityMonitor.ImpossibleGroundSpeedKt) - a teleport, scenery reload, or slew the
    /// sim did not otherwise report. Detected independently of <see cref="SlewDetected"/> so a
    /// missing or misreported slew simvar can't hide a jump. Also means the sector is not valid
    /// for payment - see <see cref="SlewDetected"/> for how callers must treat that.
    /// </summary>
    public bool PositionJumpDetected { get; set; }

    public decimal Revenue { get; set; }

    public decimal TotalCost { get; set; }

    /// <summary>
    /// True once this flight's completion ledger lines (or the deliberate decision to post none -
    /// see <see cref="SlewDetected"/>/<see cref="PositionJumpDetected"/>) have been written. The
    /// single idempotency gate for <c>FlightEconomicsPoster.PostCompletionAsync</c>: a retry,
    /// reconnect, or crash rehydration that calls completion again for the same flight is a no-op
    /// once this is true, so ledger lines are posted exactly once no matter how many times
    /// completion is invoked. Fuel is posted separately, at <see cref="FlightStatus.InProgress"/>
    /// start, because fuel is charged when it is bought rather than when it is burned, so it isn't
    /// gated by this flag.
    /// </summary>
    public bool RevenuePosted { get; set; }

    /// <summary>
    /// Human-readable reason this virtual-pilot occurrence could not fly - e.g. "G-OLAF is still at
    /// EGPF from Tuesday" - set for <see cref="FlightStatus.Skipped"/>, <see cref="FlightStatus.Cancelled"/>
    /// and <see cref="FlightStatus.Suspended"/> flights. Null for every other status, including a
    /// normal Completed flight. Conflicts are always explained in words: a sector that silently
    /// did not happen is the worst possible outcome for the player.
    /// </summary>
    public string? UnflyableReason { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
