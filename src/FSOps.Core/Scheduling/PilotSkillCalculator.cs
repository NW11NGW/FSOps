using FSOps.Core.Economy;

namespace FSOps.Core.Scheduling;

/// <summary>
/// Derives a pilot's current <see cref="Entities.Pilot.SkillRating"/> from career hours flown
/// (diminishing-returns growth, capped below perfect) and idle time since they last flew (decay) -
/// see docs/PLAN.md "Progression - reputation and pilot skill", point 3. Pure and fully RECOMPUTED
/// every call, never incremented: the same <c>(hoursFlown, lastFlewUtc, now)</c> always produces
/// the same SkillRating, so calling this any number of times for the same inputs can never move a
/// pilot's skill twice - the idempotency the wall-clock catch-up model needs, without relying on a
/// gate at every call site.
/// <para>
/// <b>Growth alone</b> (call with <paramref name="lastFlewUtc"/> equal to <paramref name="now"/>,
/// i.e. "just landed") is what every completion path uses to update a pilot the moment they fly -
/// see <see cref="Server.Services.MaintenancePoster"/>. Applies to every pilot, player included:
/// growing this number is purely informational for a player pilot (their real flights are always
/// judged by real telemetry, never by SkillRating - see docs/PLAN.md), so there is nothing to
/// protect by excluding them, and it lets "hours flown" and "skill" grow together on the player's
/// own record too.
/// </para>
/// <para>
/// <b>Idle decay</b> only ever shows up when this is called later with a growing gap between
/// <paramref name="lastFlewUtc"/> and <paramref name="now"/> - see
/// <see cref="Server.Services.VirtualFlightResolverService"/>'s periodic pass, which is the ONLY
/// caller that ever passes a nonzero idle gap, and which deliberately skips
/// <see cref="Entities.Pilot.IsPlayer"/> pilots entirely - docs/PLAN.md's explicit "the player's
/// own pilot record never decays" requirement. Decay is therefore driven purely by
/// <paramref name="lastFlewUtc"/>, which only ever advances when THAT pilot flies (player or
/// virtual, via the growth path above) - never by how long the app itself has been open. A pilot
/// on a standing schedule keeps flying on the wall clock while the app is closed, so their
/// <see cref="Entities.Pilot.LastFlewUtc"/> stays fresh regardless of real-world elapsed time;
/// decay only ever reaches a pilot deliberately left with no schedule.
/// </para>
/// </summary>
public static class PilotSkillCalculator
{
    public static double Compute(double hoursFlown, DateTimeOffset? lastFlewUtc, DateTimeOffset now, PilotSkillConfig config)
    {
        var grown = GrowthOnly(hoursFlown, config);

        if (lastFlewUtc is not { } lastFlew)
        {
            // Never flown - nothing earned yet to decay away from; GrowthOnly at hoursFlown=0
            // already returns StartingSkill, which is exactly where decay would converge to anyway.
            return grown;
        }

        var idleHours = Math.Max(0, (now - lastFlew).TotalHours);
        var decayableHours = Math.Max(0, idleHours - config.IdleGracePeriodHours);
        if (decayableHours <= 0)
        {
            return grown;
        }

        // Half-life decay: exactly 0.5 at decayableHours == IdleDecayHalfLifeHours, matching that
        // field's name precisely (Math.Exp with the raw hours would need dividing by ln(2) first -
        // this form needs no such correction).
        var decayFactor = Math.Pow(0.5, decayableHours / config.IdleDecayHalfLifeHours);
        return config.StartingSkill + (grown - config.StartingSkill) * decayFactor;
    }

    /// <summary>
    /// The pure hours-to-skill growth curve with no idle decay applied - what a pilot's
    /// SkillRating becomes the instant they land, before any future idle time has had a chance to
    /// erode it. Diminishing returns by construction: an exponential approach to
    /// <see cref="PilotSkillConfig.SkillCap"/>, so the first <see cref="PilotSkillConfig.GrowthHalfLifeHours"/>
    /// close half the remaining gap, the next equal stretch close half of what's left, and so on -
    /// the cap is approached but never reached.
    /// </summary>
    private static double GrowthOnly(double hoursFlown, PilotSkillConfig config)
    {
        var hours = Math.Max(0, hoursFlown);
        var growthFraction = 1 - Math.Pow(0.5, hours / config.GrowthHalfLifeHours);
        return config.StartingSkill + (config.SkillCap - config.StartingSkill) * growthFraction;
    }

    /// <summary>
    /// Public entry point onto <see cref="GrowthOnly"/> - what a pilot's hours alone have earned,
    /// with no idle decay applied. Exists so a UI can show "earned 68 from experience, currently 61"
    /// when a decayed pilot's <see cref="Compute"/> result sits below what their hours alone would
    /// give them - decay must be visible and explained, not just a smaller number (docs/PLAN.md
    /// "Progression - reputation and pilot skill", point 3).
    /// </summary>
    public static double ComputeEarnedSkill(double hoursFlown, PilotSkillConfig config) => GrowthOnly(hoursFlown, config);
}
