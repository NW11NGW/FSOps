using System.Threading.Channels;

namespace FSOps.Sim;

/// <summary>
/// The bounded, drop-oldest channel every <see cref="ISimSource"/> publishes into. Bounded so a
/// slow consumer can never make the sim (or replay) thread block waiting for space; drop-oldest
/// because the newest sample is always the most useful one - if nobody has read in a while,
/// falling behind on stale data is worse than losing it.
/// </summary>
public static class SimTelemetryChannel
{
    public const int Capacity = 256;

    public static Channel<TelemetrySample> Create() =>
        Channel.CreateBounded<TelemetrySample>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
        });
}
