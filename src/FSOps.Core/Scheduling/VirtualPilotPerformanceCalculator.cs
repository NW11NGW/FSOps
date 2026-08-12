namespace FSOps.Core.Scheduling;

/// <summary>
/// A virtual pilot's flight has no real telemetry, so landing quality and delay variance are
/// generated instead - deterministically, from the pilot's <c>SkillRating</c> (0-100) via a seeded
/// hash, exactly like <see cref="FSOps.Core.Economy.FuelPricing"/>'s price walk: same world seed,
/// schedule entry and occurrence always produce exactly the same result, so the same flight always
/// resolves identically, including after the app has been closed for days. Hand-rolled FNV-1a
/// rather than System.Random for the same reason FuelPricing gives - no dependency on any
/// particular .NET random-number implementation staying stable across versions.
/// <para>
/// <b>Revenue never depends on this.</b> Only <see cref="DelayMinutes"/> feeds back into the
/// economy, and only on the cost side (extra flightHours -&gt; more crew/maintenance cost via
/// FlightEconomicsCalculator) - never revenue, which stays exactly <c>paxBooked x fare</c> per
/// FlightEconomicsResult's own invariant. This is what makes a virtual pilot earn LESS per sector
/// than an engaged player on average (delay variance eats into cost, and there is no equivalent of
/// a player's landing-quality upside). Passive income has to be real but not trivial: hiring
/// should be attractive without making flying yourself pointless. All of it achieved without ever
/// touching the "revenue comes from passengers, never from the clock" integrity rule.
/// </para>
/// </summary>
public static class VirtualPilotPerformanceCalculator
{
    /// <summary>Best-case landing rate (fpm) a maximally-skilled pilot lands at - a firm-but-smooth
    /// touchdown, not a greaser (which would need randomness to ever look bad).</summary>
    private const double BestCaseLandingFpm = 150;

    /// <summary>Worst-case landing rate added on top of the best case at zero skill - a hard but
    /// still perfectly safe landing, matching the kind of "informational, never penalised" spirit
    /// the rest of the app applies to things it doesn't want to feel punishing.</summary>
    private const double WorstCaseExtraLandingFpm = 450;

    /// <summary>Maximum extra block-time minutes a zero-skill pilot's delay variance can add. A
    /// maximally-skilled pilot adds none.</summary>
    private const double MaxDelayVarianceMinutes = 18;

    public static VirtualFlightPerformance Resolve(int worldSeed, Guid scheduleEntryId, DateTimeOffset departureUtc, double skillRating)
    {
        var clampedSkill = Math.Clamp(skillRating, 0, 100);
        var skillFraction = clampedSkill / 100.0;

        var landingNoise = UnitNoise(worldSeed, scheduleEntryId, departureUtc, salt: 1);
        var delayNoise = UnitNoise(worldSeed, scheduleEntryId, departureUtc, salt: 2);
        var gForceNoise = UnitNoise(worldSeed, scheduleEntryId, departureUtc, salt: 3);

        // Higher skill both lowers the worst-case ceiling AND biases the random draw toward the
        // best case - so a highly-skilled pilot isn't just "usually smooth with an occasional bad
        // one", their bad days are less bad too.
        var landingFpm = BestCaseLandingFpm + WorstCaseExtraLandingFpm * (1 - skillFraction) * landingNoise;
        var delayMinutes = MaxDelayVarianceMinutes * (1 - skillFraction) * delayNoise;
        var gForce = 1.0 + 0.6 * (1 - skillFraction) * gForceNoise;

        return new VirtualFlightPerformance(
            LandingFpm: Math.Round(landingFpm, 1),
            DelayMinutes: Math.Round(delayMinutes, 1),
            GForce: Math.Round(gForce, 2));
    }

    /// <summary>Stable FNV-1a hash over (seed, entry, occurrence, salt), mapped to [0, 1) - the
    /// same technique as FuelPricing.UnitNoise, with an extra salt so the three outputs above don't
    /// all move together from the same draw.</summary>
    private static double UnitNoise(int worldSeed, Guid scheduleEntryId, DateTimeOffset departureUtc, int salt)
    {
        unchecked
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;
            hash = MixInt(hash, worldSeed, prime);
            hash = MixInt(hash, salt, prime);

            foreach (var b in scheduleEntryId.ToByteArray())
            {
                hash = (hash ^ b) * prime;
            }

            hash = MixLong(hash, departureUtc.UtcTicks, prime);

            return (hash % 1_000_000UL) / 1_000_000.0;
        }
    }

    private static ulong MixInt(ulong hash, int value, ulong prime)
    {
        unchecked
        {
            foreach (var b in BitConverter.GetBytes(value))
            {
                hash = (hash ^ b) * prime;
            }

            return hash;
        }
    }

    private static ulong MixLong(ulong hash, long value, ulong prime)
    {
        unchecked
        {
            foreach (var b in BitConverter.GetBytes(value))
            {
                hash = (hash ^ b) * prime;
            }

            return hash;
        }
    }
}

/// <summary>Synthesized outcome for one virtual-pilot occurrence - see
/// <see cref="VirtualPilotPerformanceCalculator.Resolve"/>.</summary>
public sealed record VirtualFlightPerformance(double LandingFpm, double DelayMinutes, double GForce);
