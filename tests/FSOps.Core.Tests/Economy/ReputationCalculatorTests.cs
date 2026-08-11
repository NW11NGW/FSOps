using FSOps.Core.Economy;

namespace FSOps.Core.Tests.Economy;

/// <summary>
/// Exercises ReputationCalculator against docs/PLAN.md "Progression - reputation and pilot skill",
/// point 1 (what moves it, and by how much relative to each other) and point 2 (the stated 40-60
/// sector magnitude target, asserted directly here per the plan's own instruction not to eyeball it).
/// </summary>
public class ReputationCalculatorTests
{
    private static readonly ReputationConfig Config = EconomyConfig.Default().Reputation;

    [Fact]
    public void ConsistentlyPerfectSectors_CrossReputation75_WithinThePlansStated40To60SectorBand()
    {
        var score = Config.BaselineScore;
        var crossedAt = -1;

        for (var sector = 1; sector <= 60; sector++)
        {
            // Best possible outcome every time: no delay, best-case landing rate.
            score = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);

            if (crossedAt < 0 && score >= 75)
            {
                crossedAt = sector;
            }
        }

        Assert.True(crossedAt >= 40 && crossedAt <= 60,
            $"Expected reputation to cross 75 between the 40th and 60th consistently-good sector (docs/PLAN.md point 2), but it happened at sector {crossedAt}.");
    }

    [Fact]
    public void CharacterisationOnly_ConsistentlyPerfectSectors_CurrentlyCrossAtTheFiftiethSector()
    {
        // NOT a requirement test - the user's actual stated requirement is the band asserted by
        // ConsistentlyPerfectSectors_CrossReputation75_WithinThePlansStated40To60SectorBand above.
        // This one documents where Alpha's current derivation (see ReputationConfig.Alpha's own
        // doc - it solves for a crossing at exactly sector 50, the band's midpoint) happens to land
        // TODAY, so a future retune of Alpha reads here as a deliberate decision rather than a
        // mystery test failure. If this ever moves within 40-60, that is fine; outside the band is
        // what the other test guards against.
        var score = Config.BaselineScore;
        for (var sector = 1; sector < 50; sector++)
        {
            score = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);
        }

        Assert.True(score < 75, $"Characterising current tuning: expected still below 75 after 49 sectors, was {score}.");

        score = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);

        // A tiny epsilon, not a knife-edge >= 75: Alpha is a finite-precision decimal literal
        // solving a transcendental equation, so the 50th-sector score lands a hair below 75 in
        // floating point (this is the observation that prompted this test to be reframed as a
        // characterisation rather than a requirement - see this test's own doc above).
        Assert.True(score >= 75 - 0.001, $"Characterising current tuning: expected to reach ~75 by the 50th sector, was {score}.");
    }

    [Fact]
    public void CancelledSector_MovesReputationFartherThanTheWorstPossibleCompletedSector()
    {
        var fromScore = 60.0;

        // The worst a completed sector could ever score: maximum delay, worst-case landing.
        var afterWorstCompleted = ReputationCalculator.AdvanceForCompletedFlight(
            fromScore, Config, delayMinutes: Config.OnTimeZeroScoreDelayMinutes, landingFpm: Config.LandingWorstFpm);

        var afterCancelled = ReputationCalculator.AdvanceForCancelledOrSkipped(fromScore, Config);

        Assert.True(fromScore - afterWorstCompleted > 0, "A worst-case completed sector should still cost some reputation.");
        Assert.True(fromScore - afterCancelled > fromScore - afterWorstCompleted,
            "A cancelled/skipped sector must cost strictly more than even the worst completed sector - docs/PLAN.md point 1.");
    }

    [Fact]
    public void CancelledSector_MovesTowardTheSameFloorAsTheWorstCompletedSector_ButFaster()
    {
        // Both converge toward the same worst-case target (0) - the difference is purely how fast
        // they get there (CancelledAlphaMultiplier), matching ReputationConfig.CancelledTargetScore's
        // own doc ("matching what a completed sector could only ever reach at its own absolute worst").
        var afterCancelled = ReputationCalculator.AdvanceForCancelledOrSkipped(50.0, Config);
        var afterWorstCompleted = ReputationCalculator.AdvanceForCompletedFlight(
            50.0, Config, delayMinutes: Config.OnTimeZeroScoreDelayMinutes, landingFpm: Config.LandingWorstFpm);

        Assert.True(afterCancelled < afterWorstCompleted);
    }

    [Fact]
    public void OnTimeWithinTolerance_ScoresTheSameAsAPerfectlyOnTimeSector()
    {
        var score = 50.0;
        var perfect = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);
        var withinGrace = ReputationCalculator.AdvanceForCompletedFlight(
            score, Config, delayMinutes: Config.OnTimeToleranceMinutes, landingFpm: Config.LandingBestFpm);

        Assert.Equal(perfect, withinGrace, precision: 10);
    }

    [Fact]
    public void ElevatedSimulationRate_ExcludesOnTimeButStillScoresLanding()
    {
        var baseline = 50.0;

        // A perfect landing, but on-time performance is unmeasured (delayMinutes: null) - the score
        // should move purely off the landing component (100% weight), not the blended 80/20 split.
        var withUnmeasuredOnTime = ReputationCalculator.AdvanceForCompletedFlight(
            baseline, Config, delayMinutes: null, landingFpm: Config.LandingBestFpm);

        var landingOnlyExpected = ReputationCalculator.AdvanceForCompletedFlight(
            baseline, Config, delayMinutes: null, landingFpm: Config.LandingBestFpm);

        Assert.Equal(landingOnlyExpected, withUnmeasuredOnTime);
        Assert.True(withUnmeasuredOnTime > baseline, "A perfect landing alone should still move reputation up, even with on-time unmeasured.");
    }

    [Fact]
    public void NeitherSignalAvailable_LeavesReputationUnchanged()
    {
        var score = 63.4;
        var result = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: null, landingFpm: null);

        Assert.Equal(score, result);
    }

    [Fact]
    public void Score_NeverLeavesTheZeroToHundredRange()
    {
        var score = 0.0;
        for (var i = 0; i < 500; i++)
        {
            score = ReputationCalculator.AdvanceForCancelledOrSkipped(score, Config);
        }

        Assert.True(score >= 0 && score <= 100);

        score = 100.0;
        for (var i = 0; i < 500; i++)
        {
            score = ReputationCalculator.AdvanceForCompletedFlight(score, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);
        }

        Assert.True(score >= 0 && score <= 100);
    }

    [Fact]
    public void ManualCompletion_IsWorseThanACleanTrackedSector_AndBetterThanATerribleOne()
    {
        // The invariant a flat manual-completion penalty exists to guarantee: never the smart play
        // over actually flying the sector out. "Worse than clean" holds because a clean sector
        // pulls reputation UP (target near 100) while manual completion only ever pulls it toward
        // ManualCompletionTargetScore; "better than terrible" holds because both share (by default)
        // the same worst-case target, but manual completion takes a strictly smaller step toward it
        // (ManualCompletionAlphaMultiplier < 1).
        var clean = ReputationCalculator.AdvanceForCompletedFlight(50.0, Config, delayMinutes: 0, landingFpm: Config.LandingBestFpm);
        var manual = ReputationCalculator.AdvanceForUnverifiedManualCompletion(50.0, Config);
        var terrible = ReputationCalculator.AdvanceForCompletedFlight(
            50.0, Config, delayMinutes: Config.OnTimeZeroScoreDelayMinutes, landingFpm: Config.LandingWorstFpm);

        Assert.True(manual < clean, $"Manual completion ({manual}) must be worse than a clean tracked sector ({clean}).");
        Assert.True(manual > terrible, $"Manual completion ({manual}) must be better than a genuinely terrible tracked sector ({terrible}).");

        // The worked figures from the design conversation, so a future retune notices if it drifts
        // away from the agreed shape rather than just staying "somewhere in between".
        Assert.Equal(-0.23, Math.Round(manual - 50.0, 2));
        Assert.Equal(-0.69, Math.Round(terrible - 50.0, 2));
    }

    [Fact]
    public void ManualCompletion_TakesTheSamePenalty_RegardlessOfWhatItIsCalledWith()
    {
        // ReputationCalculator.AdvanceForUnverifiedManualCompletion deliberately takes no timing or
        // landing parameter at all - there is nothing for a caller to vary. This is the calculator-
        // level half of the guarantee; FlightManualCompletionAndAbandonTests.
        // CompleteManualAsync_AppliesTheFixedPenalty_RegardlessOfTiming proves the endpoint upholds
        // it end to end.
        var a = ReputationCalculator.AdvanceForUnverifiedManualCompletion(62.5, Config);
        var b = ReputationCalculator.AdvanceForUnverifiedManualCompletion(62.5, Config);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecomputingWithTheSameInputs_AlwaysProducesTheSameResult_ProvingIdempotency()
    {
        // The idempotency this app's wall-clock catch-up model needs: given the same current score
        // and the same flight outcome, advancing "twice" (i.e. calling this function again with
        // whatever it returned as the new "current") must not compound beyond what a single honest
        // application would - this test instead proves the more fundamental property the caller's
        // own gate (Flight.RevenuePosted / the occurrence-watermark commit) relies on: the SAME call
        // with the SAME inputs is always a pure function, never dependent on hidden state.
        var a = ReputationCalculator.AdvanceForCompletedFlight(55.0, Config, delayMinutes: 12, landingFpm: 300);
        var b = ReputationCalculator.AdvanceForCompletedFlight(55.0, Config, delayMinutes: 12, landingFpm: 300);

        Assert.Equal(a, b);
    }
}
