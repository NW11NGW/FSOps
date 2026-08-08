using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class BlockTimeCalculatorTests
{
    [Fact]
    public void BlockHours_NormalOutAndIn_ReturnsTheElapsedHours()
    {
        var outUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var inUtc = outUtc.AddMinutes(91.5); // 1.525 hours

        Assert.Equal(1.525, BlockTimeCalculator.BlockHours(outUtc, inUtc), precision: 3);
    }

    [Fact]
    public void BlockHours_OutMissing_ReturnsZero()
    {
        Assert.Equal(0, BlockTimeCalculator.BlockHours(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BlockHours_InMissing_ReturnsZero()
    {
        Assert.Equal(0, BlockTimeCalculator.BlockHours(DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void BlockHours_InBeforeOut_ReturnsZeroRatherThanNegative()
    {
        var outUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var inUtc = outUtc.AddMinutes(-5);

        Assert.Equal(0, BlockTimeCalculator.BlockHours(outUtc, inUtc));
    }

    [Fact]
    public void BlockHours_InEqualsOut_ReturnsZero()
    {
        var t = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, BlockTimeCalculator.BlockHours(t, t));
    }
}
