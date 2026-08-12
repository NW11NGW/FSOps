namespace FSOps.Core.Planning;

/// <summary>
/// One airframe as far as range is concerned: what it is, how far it goes, and whether it is
/// currently reserved to the player. Deliberately not <see cref="Entities.FleetAircraft"/> - this
/// keeps <see cref="RouteRangeAssessor"/> a pure function over plain data the caller has already
/// loaded, and keeps the range question free of anything to do with type MATCHING (see
/// docs/PLAN.md: family-level type matching is informational and never penalised - range is a
/// physical limit of a specific airframe and must never become a back-door type penalty).
/// </summary>
public sealed record RangeCandidateAircraft(string Registration, string TypeName, int RangeNm, bool ReservedForPlayer);

/// <summary>
/// What the airline as a whole can do about a given sector length. The distinction that matters is
/// between "you can't fly this yourself yet" (fixable in one click by reserving) and "nothing you
/// own can do this at all" (only fixable by acquiring an aircraft) - the first must never block.
/// </summary>
public enum RouteRangeVerdict
{
    /// <summary>Nothing to assess: no distance, or the airline has no fleet at all.</summary>
    NotAssessed,

    /// <summary>An aircraft already reserved to the player has the legs for it. Nothing to say.</summary>
    ReservedCanFly,

    /// <summary>Nothing reserved can reach it, but something in the fleet can. Guidance, never a block.</summary>
    RequiresReservation,

    /// <summary>No aircraft in the fleet can reach it. The only genuine block.</summary>
    BeyondFleet,
}

/// <summary>
/// The outcome of asking "can this airline fly this far?", including the exact sentence to show.
/// The message is built here rather than in the API or the UI so route creation, the route preview
/// and any future caller cannot drift into saying different things about the same fleet.
/// </summary>
public sealed record RouteRangeAssessment(
    RouteRangeVerdict Verdict,
    bool Blocking,
    string? Message,
    string? AircraftRegistration,
    string? AircraftTypeName,
    double? OperationalRangeNm)
{
    public static RouteRangeAssessment NotAssessed { get; } =
        new(RouteRangeVerdict.NotAssessed, Blocking: false, Message: null, AircraftRegistration: null, AircraftTypeName: null, OperationalRangeNm: null);
}

/// <summary>
/// Answers the range question about the AIRLINE, not about one aircraft.
/// <para>
/// The bug this exists to kill: route creation used to compare the sector against a single
/// arbitrarily-chosen aircraft type and refuse the route in that type's name - "beyond the Airbus
/// A320's practical operating range" - which is wrong in both directions. The player may own (or be
/// able to reserve) something with the legs for it, and the type being named may be one they could
/// never have flown that sector in anyway. A range verdict is only meaningful across the whole
/// fleet, and only genuinely blocking when NOTHING in the fleet can do it.
/// </para>
/// <para>
/// Reservation is reused as the gate rather than reinvented (docs/PLAN.md "3a": reservation is the
/// sole two-way gate on who may fly what), so the three answers line up exactly with the three
/// things a player can actually do next: nothing, reserve, or acquire.
/// </para>
/// Pure and deterministic - no I/O, no clock, no randomness - so every branch is unit-testable with
/// exact expected strings.
/// </summary>
public static class RouteRangeAssessor
{
    /// <summary>
    /// Real aircraft rarely fly right up to their catalogue range once reserves and payload are
    /// accounted for, so "can reach" uses this fraction of the published range rather than the raw
    /// figure. Long-standing existing behaviour - the single definition of it now lives here and
    /// <see cref="RoutePreviewCalculator.OperationalRangeFactor"/> defers to it.
    /// </summary>
    public const double OperationalRangeFactor = 0.85;

    /// <summary>The published range derated to what the aircraft will actually plan to.</summary>
    public static double OperationalRangeNm(int publishedRangeNm) => publishedRangeNm * OperationalRangeFactor;

    /// <summary>Whether an aircraft of this published range can reach that far.</summary>
    public static bool CanReach(int publishedRangeNm, double distanceNm) => distanceNm <= OperationalRangeNm(publishedRangeNm);

    /// <summary>
    /// Assesses a sector against every aircraft the airline owns. Candidate ordering is by
    /// operational range descending, then registration, so the same fleet always names the same
    /// aircraft no matter what order the caller loaded them in.
    /// </summary>
    public static RouteRangeAssessment Assess(double distanceNm, IReadOnlyList<RangeCandidateAircraft> fleet)
    {
        if (distanceNm <= 0 || fleet.Count == 0)
        {
            return RouteRangeAssessment.NotAssessed;
        }

        var ordered = fleet
            .OrderByDescending(a => a.RangeNm)
            .ThenBy(a => a.Registration, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reserved = ordered.FirstOrDefault(a => a.ReservedForPlayer && CanReach(a.RangeNm, distanceNm));
        if (reserved is not null)
        {
            // Nothing to warn about at all - the player can fly this today.
            return new RouteRangeAssessment(
                RouteRangeVerdict.ReservedCanFly, Blocking: false, Message: null,
                reserved.Registration, reserved.TypeName, OperationalRangeNm(reserved.RangeNm));
        }

        var capable = ordered.FirstOrDefault(a => CanReach(a.RangeNm, distanceNm));
        if (capable is not null)
        {
            // NOT a block. This is the case a player hits when they own the right aircraft but
            // haven't reserved it, and it used to be a dead end. Name the aircraft and the one
            // action that changes the answer.
            return new RouteRangeAssessment(
                RouteRangeVerdict.RequiresReservation, Blocking: false,
                $"This route is {distanceNm:F0} nm. Nothing reserved to you can fly it, but {capable.Registration} " +
                $"({capable.TypeName}) has the range - reserve it on the Fleet page to fly it yourself, or roster it " +
                "to a virtual pilot as it is.",
                capable.Registration, capable.TypeName, OperationalRangeNm(capable.RangeNm));
        }

        var longestLegged = ordered[0];
        return new RouteRangeAssessment(
            RouteRangeVerdict.BeyondFleet, Blocking: true,
            $"This route is {distanceNm:F0} nm, beyond every aircraft in your fleet. Your longest-legged is " +
            $"{longestLegged.Registration} ({longestLegged.TypeName}) at about {OperationalRangeNm(longestLegged.RangeNm):F0} nm " +
            "once reserves are accounted for - add an aircraft with the range from the Fleet page to fly this route.",
            longestLegged.Registration, longestLegged.TypeName, OperationalRangeNm(longestLegged.RangeNm));
    }
}
