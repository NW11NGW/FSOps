using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class FuelBurnResolverTests
{
    public class MeasureTests
    {
        [Fact]
        public void EnginesObservedRunning_ReturnsTheAccumulatedDecreaseOutright()
        {
            var measured = FuelBurnResolver.Measure(
                engineStartFuelKg: 3000, accumulatedDecreaseKg: 1800, firstSampleFuelKg: 3200, lastFuelKg: 1200);

            // The accumulated figure wins outright - NOT engineStartFuelKg (3000) minus
            // lastFuelKg (1200) = 1800, which happens to match here by construction of the test
            // data, but the point is accumulatedDecreaseKg is returned directly, unrecomputed.
            Assert.Equal(1800, measured);
        }

        [Fact]
        public void EnginesNeverObservedRunning_FallsBackToFirstSampleMinusLast()
        {
            var measured = FuelBurnResolver.Measure(
                engineStartFuelKg: null, accumulatedDecreaseKg: 0, firstSampleFuelKg: 2600, lastFuelKg: 2450);

            Assert.Equal(150, measured);
        }

        [Fact]
        public void NoTelemetryAtAll_ReturnsNull()
        {
            var measured = FuelBurnResolver.Measure(
                engineStartFuelKg: null, accumulatedDecreaseKg: 0, firstSampleFuelKg: null, lastFuelKg: 0);

            Assert.Null(measured);
        }

        [Fact]
        public void EnginesNeverObserved_ATopUpBeforeStartup_CanProduceANegativeFigure_ForResolveToCatch()
        {
            // A rise before engine start (a menu fuel set, say) with no genuine engine-start
            // baseline ever recorded - Measure itself doesn't guard against this, that's Resolve's
            // job (see ResolveTests below).
            var measured = FuelBurnResolver.Measure(
                engineStartFuelKg: null, accumulatedDecreaseKg: 0, firstSampleFuelKg: 1000, lastFuelKg: 4000);

            Assert.Equal(-3000, measured);
        }

        [Fact]
        public void AccumulatedDecreaseIsUsed_EvenWhenItHappensToBeZero()
        {
            // Engines started but nothing was ever observed to decrease (an abandon one sample
            // after engine start, say) - a real, honest zero, not tier-2's subtraction.
            var measured = FuelBurnResolver.Measure(
                engineStartFuelKg: 2000, accumulatedDecreaseKg: 0, firstSampleFuelKg: 2000, lastFuelKg: 2000);

            Assert.Equal(0, measured);
        }
    }

    public class ResolveTests
    {
        [Fact]
        public void PlausiblePositiveBurn_IsTrusted()
        {
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: 1800, plausibilityCeilingKg: 2500, fallbackKg: 2500);

            Assert.Equal(1800, resolution.BilledKg);
            Assert.False(resolution.UsedFallback);
        }

        [Fact]
        public void NullMeasurement_FallsBack()
        {
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: null, plausibilityCeilingKg: 2500, fallbackKg: 2500);

            Assert.Equal(2500, resolution.BilledKg);
            Assert.True(resolution.UsedFallback);
        }

        /// <summary>Never a credit, however the measurement arrived at a negative figure (a
        /// pre-engine-start top-up caught by tier 2, say).</summary>
        [Fact]
        public void NegativeMeasurement_FallsBack_NeverACredit()
        {
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: -3000, plausibilityCeilingKg: 2500, fallbackKg: 2500);

            Assert.Equal(2500, resolution.BilledKg);
            Assert.True(resolution.UsedFallback);
            Assert.True(resolution.BilledKg >= 0);
        }

        [Fact]
        public void ZeroMeasurement_FallsBack_RatherThanBillingNothing()
        {
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: 0, plausibilityCeilingKg: 2500, fallbackKg: 2500);

            Assert.Equal(2500, resolution.BilledKg);
            Assert.True(resolution.UsedFallback);
        }

        /// <summary>A reading wildly beyond what the sector could plausibly have burned (a sim
        /// reset that zeroes the tank mid-flight, say) must never produce a wild charge.</summary>
        [Fact]
        public void ImplausiblyLargeMeasurement_FallsBack_RatherThanBillingAWildFigure()
        {
            // Ceiling is 2500 * 3 = 7500 - a 9000 kg "burn" is comfortably beyond it.
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: 9000, plausibilityCeilingKg: 2500, fallbackKg: 2500);

            Assert.Equal(2500, resolution.BilledKg);
            Assert.True(resolution.UsedFallback);
        }

        [Fact]
        public void JustUnderThePlausibilityCeiling_IsStillTrusted()
        {
            // Ceiling is 1000 * 3 = 3000 exactly. A hair under it must still read as a real,
            // trusted measurement - the guard exists to catch bad data, not to second-guess a big
            // but genuine diversion-driven burn.
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: 2999, plausibilityCeilingKg: 1000, fallbackKg: 1000);

            Assert.Equal(2999, resolution.BilledKg);
            Assert.False(resolution.UsedFallback);
        }

        /// <summary>
        /// The fallback amount is deliberately a SEPARATE parameter from the plausibility ceiling -
        /// a completed sector falls back to its own full planned charge, while an abandoned flight
        /// falls back to zero (see FlightEndpoints.AbandonAsync), but both must be caught by the
        /// exact same ceiling logic. Proves the two are genuinely independent.
        /// </summary>
        [Fact]
        public void FallbackAmountAndPlausibilityCeiling_AreIndependentOfEachOther()
        {
            var trusted = FuelBurnResolver.Resolve(measuredBurnKg: 2000, plausibilityCeilingKg: 5000, fallbackKg: 0);
            Assert.Equal(2000, trusted.BilledKg);
            Assert.False(trusted.UsedFallback);

            var untrusted = FuelBurnResolver.Resolve(measuredBurnKg: null, plausibilityCeilingKg: 5000, fallbackKg: 0);
            Assert.Equal(0, untrusted.BilledKg);
            Assert.True(untrusted.UsedFallback);
        }

        [Fact]
        public void NegativeFallback_IsFlooredAtZero()
        {
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: null, plausibilityCeilingKg: 1000, fallbackKg: -50);

            Assert.Equal(0, resolution.BilledKg);
        }

        [Fact]
        public void ZeroCeiling_StillFallsBackOnAnUnusableMeasurement_RatherThanThrowing()
        {
            // A same-airport/zero-distance edge case could plausibly hand in a zero ceiling - must
            // degrade gracefully (no upper bound to enforce) rather than misbehave.
            var resolution = FuelBurnResolver.Resolve(measuredBurnKg: null, plausibilityCeilingKg: 0, fallbackKg: 0);

            Assert.Equal(0, resolution.BilledKg);
            Assert.True(resolution.UsedFallback);
        }
    }
}
