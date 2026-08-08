namespace FSOps.Sim.Fake;

public sealed class FakeSimSourceOptions
{
    /// <summary>Path to the replay JSON file (see <see cref="FakeFlightScript"/>).</summary>
    public string ReplayFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Speeds up the replay relative to wall-clock time. 1.0 plays the scripted flight at its
    /// real duration; 60.0 plays an hour-long sector in about a minute, which is how the whole
    /// app - including hour-long sectors - gets exercised in a test run with no simulator.
    /// </summary>
    public double TimeCompressionFactor { get; init; } = 1.0;

    /// <summary>When the last keyframe is reached, start again from the beginning instead of holding.</summary>
    public bool Loop { get; init; } = true;

    /// <summary>How often a new sample is produced, in wall-clock time.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Sim-time position to start replay from, for development/seeking. 0 is the beginning.</summary>
    public double StartAtSeconds { get; init; }
}
