namespace FSOps.Core.Time;

/// <summary>
/// The economy engine's only source of "now". Real code reads the system clock
/// (<see cref="SystemClock"/>); tests inject a fake so wall-clock behaviour - catch-up after being
/// closed for days, a clock that jumps backwards - can be driven deterministically instead of
/// depending on how long a test happens to take to run.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
