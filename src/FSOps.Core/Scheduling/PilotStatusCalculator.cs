using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Scheduling;

/// <summary>
/// Works out what a pilot is doing right now. Pure, and <b>derived on every read rather than
/// stored</b>, for the same reason <see cref="PilotSkillCalculator"/> is: a status column would be
/// written at one moment and read at another, and every moment in between is a chance for it to be
/// wrong. A pilot who took off after the last write would still read Available; one whose flight
/// landed while the app was closed would still read Flying. There is no write frequent enough to
/// fix that, because the thing being described (a flight in progress, time passing) changes without
/// anything in the app happening at all. Deriving it costs one query and is right by construction.
/// <para>
/// <b>Flying</b> is decided by the flight row (<see cref="FlightStatus.InProgress"/>), never by
/// anything on the pilot - the flight is the stronger source, and it is the same source
/// <c>PilotEndpoints.ReleaseAsync</c> has always used to refuse releasing an airborne pilot. In
/// practice this reaches the player pilot far more often than a virtual one, because
/// <c>VirtualFlightResolverService</c> resolves a scheduled occurrence in a single pass and writes
/// it straight to Completed - a virtual sector is never observed mid-air. That is not a reason to
/// special-case it here: if a virtual flight ever does sit InProgress, "Flying" is the honest answer.
/// </para>
/// <para>
/// <b>Inactive</b> means idle on purpose, not merely quiet: no legs on their weekly schedule AND
/// nothing flown for longer than <see cref="PilotSkillConfig.IdleGracePeriodHours"/>. That
/// threshold is deliberately the SAME one idle skill decay uses, rather than a second number
/// invented here - the roster already shows a decay warning driven by it, and a row that reads
/// "Available" next to "skill is decaying" (or "Inactive" next to "flew yesterday") is the app
/// contradicting itself in two adjacent columns. A pilot with a standing schedule is never Inactive
/// however long the gap: their pattern is still rolling and will produce their next sector on its
/// own, which is the whole promise of a standing schedule.
/// </para>
/// <para>
/// <b>The player pilot is never Inactive.</b> Idle decay already skips them on purpose - their real
/// flying is judged by real telemetry, so decaying their record would punish them while changing
/// nothing - and the same reasoning applies with more force to a label: "Inactive" against the
/// human's own name is the app telling them off for not playing, over a period it measured with a
/// wall clock they were not asked to watch.
/// </para>
/// </summary>
public static class PilotStatusCalculator
{
    /// <param name="isPlayer">See <see cref="Pilot.IsPlayer"/> - the human's own record.</param>
    /// <param name="hasFlightInProgress">Whether a <see cref="Flight"/> owned by this pilot is
    /// currently <see cref="FlightStatus.InProgress"/>.</param>
    /// <param name="hasScheduledLegs">Whether this pilot has a standing weekly schedule with at
    /// least one leg on it. A saved schedule with no legs left counts as no schedule - an empty
    /// pattern produces nothing, so the pilot really is idle.</param>
    /// <param name="lastFlewUtc">See <see cref="Pilot.LastFlewUtc"/>. Null for a pilot who has
    /// never flown, in which case idleness is measured from <paramref name="createdUtc"/> - a pilot
    /// hired an hour ago is not idle, one hired two months ago and never given anything to do is.</param>
    public static PilotStatus Resolve(
        bool isPlayer,
        bool hasFlightInProgress,
        bool hasScheduledLegs,
        DateTimeOffset? lastFlewUtc,
        DateTimeOffset createdUtc,
        DateTimeOffset now,
        PilotSkillConfig config)
    {
        if (hasFlightInProgress)
        {
            return PilotStatus.Flying;
        }

        if (isPlayer || hasScheduledLegs)
        {
            return PilotStatus.Available;
        }

        var since = lastFlewUtc ?? createdUtc;
        var idleHours = Math.Max(0, (now - since).TotalHours);
        return idleHours > config.IdleGracePeriodHours ? PilotStatus.Inactive : PilotStatus.Available;
    }
}
