namespace FSOps.Core.Entities;

/// <summary>
/// One virtual pilot's standing weekly schedule - the aggregate root that owns their
/// <see cref="PilotScheduleEntry"/> rows. See docs/PLAN.md "Virtual pilot scheduling - standing
/// assignments and the schedule builder": each pilot gets exactly one week-long calendar that
/// repeats indefinitely (there is no end date - the week IS the repeating unit), so this row exists
/// purely to give the entries a stable owner and a place to hang audit timestamps; all the actual
/// schedule content lives on the entries. Created lazily (the first successful
/// <c>PUT /pilots/{id}/schedule</c> for a pilot) rather than at hire time, so a freshly hired pilot
/// with no schedule yet simply has no row here.
/// </summary>
public class PilotSchedule
{
    public Guid Id { get; set; }

    /// <summary>One schedule per pilot - enforced by a unique index (see PilotScheduleConfiguration).</summary>
    public Guid PilotId { get; set; }

    public Guid AirlineId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Bumped every time the entry set is replaced (PUT /pilots/{id}/schedule) - purely
    /// informational, shown in the UI as "last edited".</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }

    /// <summary>
    /// When true (the default), an occurrence on this schedule whose aircraft is grounded for a
    /// maintenance check is recorded as <see cref="FlightStatus.Suspended"/> instead of being
    /// skipped or cancelled - no ledger line, and the schedule simply resumes on its own once the
    /// aircraft comes out of maintenance. See docs/PLAN.md "A schedule option: suspend during
    /// maintenance and resume automatically" - without this, a 14-day True-life C-check against a
    /// daily schedule would charge fourteen cancellation fees for something the player could not
    /// have avoided. Defaulted ON (see PilotScheduleConfiguration's explicit
    /// <c>HasDefaultValue(true)</c> - a plain EF-scaffolded bool default would otherwise land on
    /// false, silently reintroducing the fourteen-fee bug for every existing schedule).
    /// <para>
    /// Deliberately scoped to the maintenance-grounded case only: an occurrence that can't fly
    /// because the aircraft is still away from an earlier leg, or already airborne, is a scheduling
    /// problem the player caused and still skips/cancels normally either way - "the distinction
    /// that matters: a cancellation fee is for a schedule the player built badly, not for
    /// maintenance the simulation imposed" (docs/PLAN.md, same section).
    /// </para>
    /// </summary>
    public bool AutoSuspendOnMaintenance { get; set; } = true;

    public DateTimeOffset? DeletedUtc { get; set; }
}

/// <summary>
/// One leg in a pilot's standing weekly schedule - see <see cref="PilotSchedule"/>'s class doc. A
/// day can hold a chain of several entries (e.g. EGGD-&gt;EGPH-&gt;EGSS-&gt;EGPF-&gt;EGGD), each one
/// a separate row with its own departure time; <see cref="VirtualFlightResolverService"/> in
/// FSOps.Server walks these in (day, time) order to resolve each week's occurrences.
/// <para>
/// Deliberately holds no explicit ordering/sequence field: a day's chain is fully determined by
/// sorting its entries on <see cref="DepartureTimeUtc"/>, and overlapping departure times on the
/// same aircraft are rejected by <c>PilotScheduleValidator</c> at save time, so ordering can never
/// be ambiguous.
/// </para>
/// </summary>
public class PilotScheduleEntry
{
    public Guid Id { get; set; }

    public Guid PilotScheduleId { get; set; }

    /// <summary>Sunday=0 .. Saturday=6, matching <see cref="System.DayOfWeek"/>'s own underlying
    /// values - stored as a plain int (see the project's "never trust an EF-scaffolded default"
    /// rule: an enum stored via HasConversion&lt;string&gt;() risks a silent "" default, which a
    /// plain int column cannot).</summary>
    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Time of day (UTC) this leg departs.</summary>
    public TimeSpan DepartureTimeUtc { get; set; }

    /// <summary>The directional route leg this entry flies (see docs/PLAN.md "Routes are always
    /// bidirectional pairs" - this references one specific directional Route row, e.g. EGGD-&gt;EGPH,
    /// not the round-trip pair).</summary>
    public Guid RouteId { get; set; }

    public Guid FleetAircraftId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
