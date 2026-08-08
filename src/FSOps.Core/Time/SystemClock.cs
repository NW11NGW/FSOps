namespace FSOps.Core.Time;

/// <summary>The real clock - the only production implementation of <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
