namespace FSOps.Core.Economy;

/// <summary>
/// Per-airport fuel price: the global base price, scaled by a regional multiplier, then nudged
/// by a deterministic pseudo-random "walk" over time so prices drift day to day instead of
/// sitting still, without ever needing network data or a mutable price table. Same world seed,
/// airport and date always produce exactly the same price - no exceptions, and no dependency on
/// any particular .NET random-number implementation staying stable across versions, since the
/// noise is a hand-rolled stable hash rather than System.Random.
/// </summary>
public static class FuelPricing
{
    public static decimal PricePerKg(FuelConfig config, string airportIcao, string? regionKey, DateTimeOffset dateUtc, int worldSeed)
    {
        if (string.IsNullOrWhiteSpace(airportIcao))
        {
            throw new ArgumentException("Airport ICAO is required.", nameof(airportIcao));
        }

        var regionalMultiplier = ResolveRegionalMultiplier(config, regionKey);
        var dayIndex = DayIndex(dateUtc);
        var noise = WalkNoise(worldSeed, airportIcao, dayIndex, config.NoiseWindowDays);
        var volatilityFactor = 1.0 + noise * config.VolatilityAmplitude;

        var price = config.BasePricePerKg * regionalMultiplier * (decimal)volatilityFactor;
        return Math.Round(Math.Max(0m, price), 4, MidpointRounding.AwayFromZero);
    }

    private static decimal ResolveRegionalMultiplier(FuelConfig config, string? regionKey)
    {
        if (!string.IsNullOrWhiteSpace(regionKey) && config.RegionalMultipliers.TryGetValue(regionKey.Trim(), out var multiplier))
        {
            return multiplier;
        }

        return config.DefaultRegionalMultiplier;
    }

    private static int DayIndex(DateTimeOffset dateUtc) => (dateUtc.UtcDateTime.Date - DateTime.UnixEpoch).Days;

    /// <summary>
    /// A moving average of hash-derived unit noise across a trailing window of days, rescaled to
    /// [-1, 1). Adjacent days share most of their window, so the value drifts smoothly day to
    /// day like a walk, while evaluating any single date costs only O(windowDays) - no need to
    /// replay history from an epoch.
    /// </summary>
    private static double WalkNoise(int worldSeed, string airportIcao, int dayIndex, int windowDays)
    {
        var window = Math.Max(1, windowDays);
        double sum = 0;

        for (var offset = 0; offset < window; offset++)
        {
            sum += UnitNoise(worldSeed, airportIcao, dayIndex - offset);
        }

        var average = sum / window;
        return average * 2.0 - 1.0;
    }

    /// <summary>Stable FNV-1a hash over (seed, airport, day), mapped to [0, 1). Hand-rolled
    /// rather than System.Random so the sequence never changes under a future .NET upgrade.</summary>
    private static double UnitNoise(int worldSeed, string airportIcao, int dayIndex)
    {
        unchecked
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;
            hash = MixInt(hash, worldSeed, prime);

            foreach (var c in airportIcao)
            {
                hash = (hash ^ c) * prime;
            }

            hash = MixInt(hash, dayIndex, prime);

            return (hash % 1_000_000UL) / 1_000_000.0;
        }
    }

    private static ulong MixInt(ulong hash, int value, ulong prime)
    {
        unchecked
        {
            var bytes = BitConverter.GetBytes(value);
            foreach (var b in bytes)
            {
                hash = (hash ^ b) * prime;
            }

            return hash;
        }
    }
}
