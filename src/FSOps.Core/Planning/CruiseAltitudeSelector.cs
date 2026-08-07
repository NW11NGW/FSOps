namespace FSOps.Core.Planning;

/// <summary>
/// Picks a planning cruise altitude using a simplified semicircular rule. Real ATC applies the
/// odd/even rule to magnetic track; FSOps has no magnetic variation model, so true initial
/// bearing is used as an approximation - close enough for suggesting a sensible altitude in a
/// game, not a substitute for a real flight-planning tool.
/// </summary>
public static class CruiseAltitudeSelector
{
    public static int SelectCruiseAltitudeFt(double distanceNm, double initialBearingDeg, int serviceCeilingFt)
    {
        var targetThousands = distanceNm switch
        {
            < 80 => 10,
            < 150 => 18,
            < 300 => 24,
            < 600 => 32,
            _ => 36,
        };

        var ceilingThousands = Math.Max(1, serviceCeilingFt / 1000);
        targetThousands = Math.Min(targetThousands, ceilingThousands);

        var bearing = ((initialBearingDeg % 360) + 360) % 360;
        var eastbound = bearing < 180;

        // Semicircular rule: eastbound (0-179 deg) flies odd flight levels, westbound
        // (180-359 deg) flies even ones.
        if (eastbound && targetThousands % 2 == 0)
        {
            targetThousands -= 1;
        }
        else if (!eastbound && targetThousands % 2 != 0)
        {
            targetThousands -= 1;
        }

        var floor = eastbound ? 3 : 4;
        targetThousands = Math.Max(targetThousands, floor);

        return targetThousands * 1000;
    }
}
