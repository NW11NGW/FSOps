namespace FSOps.Core.Contracts;

/// <summary>
/// The deterministic noise source contract generation draws from.
///
/// <para><b>Hand-rolled FNV-1a rather than <see cref="System.Random"/>, for the same reason
/// <see cref="FSOps.Core.Economy.FuelPricing"/> is:</b> the sequence must not be allowed to change
/// under a future .NET upgrade. <c>System.Random</c>'s algorithm is explicitly not part of its
/// contract and has been changed before; a board that quietly reshuffled itself on a runtime update
/// would break every exact-value test in this feature and, worse, would silently hand a player a
/// different set of jobs from the one they were looking at.</para>
///
/// <para><b>Why a stream rather than a per-draw hash.</b> Generation asks a dozen questions in a row
/// (which origin, which aircraft, which scale, which operator name) and each must be independent of
/// the others. Threading a counter through every call site by hand is exactly the kind of
/// bookkeeping that gets one call wrong and correlates two draws that should not be - so the counter
/// lives here and advances on every draw. The whole stream is still a pure function of the seed
/// values it was constructed from: same inputs, same draws, in the same order, for ever.</para>
///
/// <para>A struct passed by <c>ref</c> rather than a class, so the advancing counter is visibly the
/// caller's state. Constructing two streams from the same seeds gives two identical sequences, which
/// is what makes a board regenerate rather than drift.</para>
/// </summary>
public struct ContractRandom
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    private readonly ulong _seed;
    private ulong _counter;

    private ContractRandom(ulong seed)
    {
        _seed = seed;
        _counter = 0;
    }

    /// <summary>
    /// A stream keyed on everything that should make a board different: the world seed, whose
    /// airline it is, which time bucket, and a purpose label so two different uses of the same
    /// bucket (the board itself, then one contract's own details) never walk the same sequence.
    /// </summary>
    public static ContractRandom For(int worldSeed, Guid airlineId, long bucket, string purpose)
    {
        var hash = OffsetBasis;
        hash = MixInt(hash, worldSeed);

        foreach (var b in airlineId.ToByteArray())
        {
            hash = (hash ^ b) * Prime;
        }

        hash = MixLong(hash, bucket);

        foreach (var c in purpose)
        {
            hash = (hash ^ c) * Prime;
        }

        return new ContractRandom(hash);
    }

    /// <summary>The next value in [0, 1). Advances the stream.</summary>
    public double NextUnit()
    {
        unchecked
        {
            var hash = MixLong(_seed, (long)_counter);
            _counter++;
            return (hash % 1_000_000_000UL) / 1_000_000_000.0;
        }
    }

    /// <summary>The next integer in [minInclusive, maxExclusive). Advances the stream.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        var span = (long)maxExclusive - minInclusive;
        return (int)(minInclusive + (long)(NextUnit() * span));
    }

    /// <summary>The next value in [min, max). Advances the stream.</summary>
    public double NextDouble(double min, double max) => min + NextUnit() * (max - min);

    /// <summary>
    /// Picks one item. Returns <c>default</c> for an empty list rather than throwing - "there was
    /// nothing to choose from" is a normal answer during generation (a player with almost no aircraft
    /// available, an origin with nothing in range) and must degrade into "no contract here", never
    /// into an exception that takes the whole board down.
    /// </summary>
    public T? Pick<T>(IReadOnlyList<T> items) =>
        items.Count == 0 ? default : items[NextInt(0, items.Count)];

    /// <summary>
    /// Picks one item by weight. Weights need not sum to anything in particular; non-positive
    /// weights are simply never chosen. Returns <c>default</c> when nothing has a positive weight.
    /// </summary>
    public T? PickWeighted<T>(IReadOnlyList<T> items, Func<T, double> weight)
    {
        if (items.Count == 0)
        {
            return default;
        }

        var total = 0.0;
        foreach (var item in items)
        {
            var w = weight(item);
            if (w > 0)
            {
                total += w;
            }
        }

        if (total <= 0)
        {
            return default;
        }

        var target = NextUnit() * total;
        var running = 0.0;
        foreach (var item in items)
        {
            var w = weight(item);
            if (w <= 0)
            {
                continue;
            }

            running += w;
            if (target < running)
            {
                return item;
            }
        }

        // Floating-point accumulation can land a hair past the end. Falling off the loop means the
        // target was effectively the last positive-weight item, so return that rather than nothing.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (weight(items[i]) > 0)
            {
                return items[i];
            }
        }

        return default;
    }

    private static ulong MixInt(ulong hash, int value)
    {
        unchecked
        {
            foreach (var b in BitConverter.GetBytes(value))
            {
                hash = (hash ^ b) * Prime;
            }

            return hash;
        }
    }

    private static ulong MixLong(ulong hash, long value)
    {
        unchecked
        {
            foreach (var b in BitConverter.GetBytes(value))
            {
                hash = (hash ^ b) * Prime;
            }

            return hash;
        }
    }
}
