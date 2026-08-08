namespace FSOps.Sim.SimConnect;

public sealed class SimConnectSourceOptions
{
    /// <summary>How long to wait between connection attempts while MSFS is not running.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Below this height above ground, sampling switches to every sim frame for landing fidelity.</summary>
    public double LowAltitudeThresholdAglFt { get; init; } = 2000;

    /// <summary>
    /// Extra height above <see cref="LowAltitudeThresholdAglFt"/> required before dropping back to
    /// the normal rate, so a flight bouncing right at the threshold does not flap the subscription.
    /// </summary>
    public double LowAltitudeHysteresisFt { get; init; } = 300;

    /// <summary>
    /// SimConnect delivers data every Nth sim frame when this is the interval. MSFS commonly runs
    /// at somewhere around 30 fps with a typical add-on load, so an interval of 6 lands close to
    /// the "roughly 5 Hz" target without depending on a frame-rate reading the sim does not expose.
    /// </summary>
    public uint NormalSimFrameInterval { get; init; } = 6;
}
