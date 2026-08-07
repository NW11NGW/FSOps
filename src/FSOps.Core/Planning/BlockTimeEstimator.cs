namespace FSOps.Core.Planning;

public record BlockTimeBreakdown(
    int TaxiOutMinutes,
    int ClimbMinutes,
    int CruiseMinutes,
    int DescentMinutes,
    int TaxiInMinutes,
    int TotalMinutes);

/// <summary>
/// Estimates block time (gate to gate) from great-circle distance and cruise speed. Climb and
/// descent are modelled as fixed phases that each consume a slice of the total distance at a
/// slower average speed than cruise; whatever distance is left over is flown at the type's
/// CruiseTasKts. Short hops that never reach the combined climb+descent distance scale those
/// phases down proportionally instead of going negative. TotalMinutes is always the sum of the
/// other (already-rounded) fields, so the breakdown and the total never disagree.
/// </summary>
public static class BlockTimeEstimator
{
    private const int TaxiOutMinutes = 10;
    private const int TaxiInMinutes = 8;
    private const int ClimbMinutes = 15;
    private const double ClimbDistanceNm = 70;
    private const int DescentMinutes = 15;
    private const double DescentDistanceNm = 75;

    public static BlockTimeBreakdown Estimate(double distanceNm, int cruiseTasKts)
    {
        var climbAndDescentDistanceNm = ClimbDistanceNm + DescentDistanceNm;

        int climbMinutes;
        int descentMinutes;
        int cruiseMinutes;

        if (distanceNm <= climbAndDescentDistanceNm)
        {
            var ratio = distanceNm / climbAndDescentDistanceNm;
            climbMinutes = RoundToInt(ClimbMinutes * ratio);
            descentMinutes = RoundToInt(DescentMinutes * ratio);
            cruiseMinutes = 0;
        }
        else
        {
            climbMinutes = ClimbMinutes;
            descentMinutes = DescentMinutes;
            var cruiseDistanceNm = distanceNm - climbAndDescentDistanceNm;
            cruiseMinutes = cruiseTasKts > 0 ? RoundToInt(cruiseDistanceNm / cruiseTasKts * 60.0) : 0;
        }

        var total = TaxiOutMinutes + climbMinutes + cruiseMinutes + descentMinutes + TaxiInMinutes;
        return new BlockTimeBreakdown(TaxiOutMinutes, climbMinutes, cruiseMinutes, descentMinutes, TaxiInMinutes, total);
    }

    private static int RoundToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
