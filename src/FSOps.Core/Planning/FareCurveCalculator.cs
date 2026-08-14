using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>One fare on the curve and everything the economy engine says it produces.</summary>
public sealed record FareCurvePoint(
    decimal Fare,
    decimal MultipleOfReferenceFare,
    int PaxBooked,
    double LoadFactor,
    decimal Revenue,
    decimal TotalCost,
    decimal NetProfit);

/// <summary>
/// The whole shape of "what happens if I charge more or less", plus the two fares worth pointing at.
/// </summary>
/// <param name="ReferenceFare">What the app would charge if the player expressed no opinion.</param>
/// <param name="Points">The sampled fares, in ascending order.</param>
/// <param name="RevenueMaximizingFare">The exact revenue-maximising fare from
/// <see cref="FareDemandModel.RevenueMaximizingFare"/> - a closed-form figure, not something read
/// off the sampled points.</param>
/// <param name="BestProfitPoint">The sampled point with the highest profit per sector. Deliberately
/// described to the player as "the best of the fares sampled" rather than "the optimum": profit
/// peaks slightly above the revenue peak (passenger charges fall away as passengers do), and pinning
/// that crossing exactly would mean inventing a search this app has no reason to trust more than the
/// curve it already shows. Ties go to the lower fare - the cheapest fare that earns the money.</param>
public sealed record FareCurve(
    decimal ReferenceFare,
    IReadOnlyList<FareCurvePoint> Points,
    decimal RevenueMaximizingFare,
    FareCurvePoint BestProfitPoint);

/// <summary>
/// Sweeps a fare across a fixed grid and reports what each one does, so a player can SEE the
/// trade-off - passengers for yield - instead of guessing at it.
///
/// <para><b>Nothing here is new economics.</b> Every point is
/// <see cref="SectorProjector.AtFare"/> over one shared <see cref="SectorPlan"/>, which is
/// <see cref="FlightEconomicsCalculator"/>, which is what the ledger posts. The only thing this
/// type decides is which fares to ask about.</para>
///
/// <para><b>Why this grid.</b> Fixed multiples of the reference fare, from
/// <see cref="MinMultiple"/> to <see cref="MaxMultiple"/> in steps of <see cref="MultipleStep"/>.
/// Anchoring to the reference fare rather than to absolute money makes the same curve shape
/// comparable between a 200 nm hop and a 2,000 nm sector. The top of the range is well clear of
/// where the peak can possibly sit: <see cref="EconomyConfig.CaptiveFareCeilingMultiple"/> bounds
/// the revenue-maximising fare, and the shipped value of that is 1.5, so the peak is always inside
/// the sampled band and the player always sees the curve turn over rather than running off the end
/// of it.</para>
/// </summary>
public static class FareCurveCalculator
{
    public const decimal MinMultiple = 0.50m;
    public const decimal MaxMultiple = 2.00m;
    public const decimal MultipleStep = 0.05m;

    public static FareCurve Calculate(EconomyConfig config, AirlineStrategyProfile strategy, SectorPlan plan)
    {
        var strategyConfig = config.GetStrategy(strategy);
        var points = new List<FareCurvePoint>();

        for (var multiple = MinMultiple; multiple <= MaxMultiple; multiple += MultipleStep)
        {
            // Rounded to the cent before pricing, not after: this is a fare a player could actually
            // type into the box, and the point must describe THAT fare rather than an unrepresentable
            // one near it.
            var fare = Math.Round(plan.ReferenceFare * multiple, 2, MidpointRounding.AwayFromZero);
            var projection = SectorProjector.AtFare(config, strategy, plan, fare);

            points.Add(new FareCurvePoint(
                fare,
                multiple,
                projection.PaxBooked,
                projection.LoadFactor,
                projection.Revenue,
                projection.TotalCost,
                projection.NetProfit));
        }

        var revenueMaximizingFare = FareDemandModel.RevenueMaximizingFare(
            config.MaxLoadFactor, strategyConfig, plan.ReferenceFare, plan.Seats, plan.MarketDemandPax, config.CaptiveFareCeilingMultiple);

        // Ties to the LOWER fare: `>` rather than `>=` while walking the grid upward, so the first
        // fare reaching the best profit wins and the answer never depends on list ordering.
        var best = points[0];
        foreach (var point in points)
        {
            if (point.NetProfit > best.NetProfit)
            {
                best = point;
            }
        }

        return new FareCurve(plan.ReferenceFare, points, Math.Round(revenueMaximizingFare, 2, MidpointRounding.AwayFromZero), best);
    }
}
