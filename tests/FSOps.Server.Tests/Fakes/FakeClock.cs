using FSOps.Core.Time;

namespace FSOps.Server.Tests.Fakes;

/// <summary>A controllable <see cref="IClock"/> so wall-clock behaviour - startup catch-up after
/// being closed for days, a clock jumping backwards - can be driven deterministically in tests
/// instead of depending on how long the test happens to take to run.</summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }
}
