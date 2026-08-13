using System.Text.Json;
using System.Text.Json.Nodes;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Fleet;

namespace FSOps.Core.Tests.Fleet;

/// <summary>
/// The pure half of repositioning an aircraft: which airports are offered, which moves are refused
/// and why, and the exact arithmetic of the fee. Exact expected values throughout, matching how the
/// rest of the economy is tested - nothing here reads a clock, a database or a random number.
/// </summary>
public class AircraftRepositionEvaluatorTests
{
    private static readonly (string, string)[] Network =
    {
        ("EGGD", "EGPH"),
        ("EGPH", "EGGD"),
        ("EGGD", "EGSS"),
        ("EGSS", "EGGD"),
    };

    private static IReadOnlyList<string> DestinationsFrom(string currentIcao) =>
        AircraftRepositionEvaluator.DestinationsFor(Network, currentIcao);

    private static AircraftRepositionAssessment Evaluate(
        string currentIcao = "EGGD",
        string? destinationIcao = "EGPH",
        bool isInFlight = false,
        bool isGroundedForMaintenance = false,
        bool isReservedForPlayer = true,
        IReadOnlyCollection<string>? destinations = null,
        bool airlineHasRoutes = true,
        decimal cost = 2_000m,
        decimal cashBalance = 60_000m) =>
        AircraftRepositionEvaluator.Evaluate(
            currentIcao,
            destinationIcao,
            isInFlight,
            isGroundedForMaintenance,
            isReservedForPlayer,
            destinations ?? DestinationsFrom(currentIcao),
            airlineHasRoutes,
            cost,
            cashBalance);

    // ----- destination derivation -------------------------------------------------------------

    [Fact]
    public void DestinationsFor_OffersEveryAirportTheAirlineServes_ExceptWhereTheAircraftAlreadyIs()
    {
        // Both directions count: EGPH and EGSS appear as arrivals as well as departures, and the
        // aircraft's own airport (EGGD) is excluded because moving somewhere you already are is not
        // a move. Sorted, so the picker's order never depends on database insertion order.
        Assert.Equal(new[] { "EGPH", "EGSS" }, DestinationsFrom("EGGD"));
    }

    [Fact]
    public void DestinationsFor_FromAnOutstation_OffersTheHubBack()
    {
        // The stranding case this whole feature exists for: an aircraft sitting at EGSS can be
        // brought back to EGGD (or sent to EGPH), even though it arrived on a one-way leg.
        Assert.Equal(new[] { "EGGD", "EGPH" }, DestinationsFrom("EGSS"));
    }

    [Fact]
    public void DestinationsFor_IsCaseAndWhitespaceInsensitive()
    {
        var destinations = AircraftRepositionEvaluator.DestinationsFor(
            new[] { (" eggd ", "egph") }, "EGGD");

        Assert.Equal(new[] { "EGPH" }, destinations);
    }

    [Fact]
    public void DestinationsFor_ASingleAirportNetwork_OffersNothing()
    {
        Assert.Empty(AircraftRepositionEvaluator.DestinationsFor(new[] { ("EGGD", "EGGD") }, "EGGD"));
    }

    // ----- the happy path and its exact arithmetic ---------------------------------------------

    [Fact]
    public void AReservedIdleAircraft_MovesForExactlyTwoThousand()
    {
        var assessment = Evaluate(cashBalance: 60_000m);

        Assert.True(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.None, assessment.Refusal);
        Assert.Equal(2_000m, assessment.Cost);
        // The exact figure the confirmation shows and the ledger must produce: 60,000 - 2,000.
        Assert.Equal(58_000m, assessment.CashAfter);
    }

    [Fact]
    public void WithNoDestinationChosenYet_TheMoveIsStillAssessed()
    {
        // How the options endpoint asks "could this aircraft move at all", before the player has
        // picked anywhere - the destination-specific checks are skipped, everything else still runs.
        var assessment = Evaluate(destinationIcao: null);

        Assert.True(assessment.CanReposition);
        Assert.Equal(58_000m, assessment.CashAfter);
    }

    [Fact]
    public void ADestinationDifferingOnlyByCase_IsAccepted()
    {
        Assert.True(Evaluate(destinationIcao: " egph ").CanReposition);
    }

    // ----- refusals ---------------------------------------------------------------------------

    [Fact]
    public void AnAircraftInFlight_IsRefused()
    {
        var assessment = Evaluate(isInFlight: true);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.InFlight, assessment.Refusal);
    }

    [Fact]
    public void AnAircraftGroundedForMaintenance_IsRefused()
    {
        var assessment = Evaluate(isGroundedForMaintenance: true);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.GroundedForMaintenance, assessment.Refusal);
    }

    [Fact]
    public void AnAircraftAvailableToVirtualPilots_IsRefused()
    {
        // Repositioning is player-only (user's decision, 2026-08-13): an aircraft not held back for
        // the player belongs to the schedule, and moving it out from under a virtual pilot is not
        // the player's to do without reserving it back first.
        var assessment = Evaluate(isReservedForPlayer: false);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.NotReservedForPlayer, assessment.Refusal);
    }

    [Fact]
    public void AnAirlineWithNoRoutes_HasNowhereToSendAnything()
    {
        var assessment = Evaluate(destinations: Array.Empty<string>(), airlineHasRoutes: false);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.NoRoutesAtAll, assessment.Refusal);
    }

    [Fact]
    public void AnAirlineWhoseWholeNetworkIsOneAirport_HasNowhereElseToGo()
    {
        // Distinct from NoRoutesAtAll: there ARE routes, they just all begin and end where the
        // aircraft already is, so the player needs a new route rather than their first one.
        var assessment = Evaluate(destinations: Array.Empty<string>(), airlineHasRoutes: true);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.NowhereElseToGo, assessment.Refusal);
    }

    [Fact]
    public void MovingAnAircraftToWhereItAlreadyIs_IsRefused()
    {
        var assessment = Evaluate(destinationIcao: "EGGD");

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.AlreadyThere, assessment.Refusal);
    }

    [Fact]
    public void AnAirportTheAirlineDoesNotServe_IsRefused()
    {
        // The core restriction: destinations come from the airline's own network, never from every
        // airport in the world. EGLL is a real airport this airline simply does not fly to.
        var assessment = Evaluate(destinationIcao: "EGLL");

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.DestinationNotServed, assessment.Refusal);
    }

    [Fact]
    public void NotEnoughCash_IsRefused_AndStillReportsWhatTheMoveWouldHaveCost()
    {
        var assessment = Evaluate(cashBalance: 1_999.99m);

        Assert.False(assessment.CanReposition);
        Assert.Equal(RepositionRefusal.InsufficientCash, assessment.Refusal);
        Assert.Equal(2_000m, assessment.Cost);
        // Reported even for the refusal, so the dialog can show exactly how far short the airline is.
        Assert.Equal(-0.01m, assessment.CashAfter);
    }

    [Fact]
    public void SpendingTheAirlinesLastPenny_IsAllowed()
    {
        // Exactly the cost, not a penny more: the same "strictly less than" stance every other
        // purchase in the app takes, asserted at the boundary so it can't drift to `<=`.
        var assessment = Evaluate(cashBalance: 2_000m);

        Assert.True(assessment.CanReposition);
        Assert.Equal(0m, assessment.CashAfter);
    }

    // ----- refusal precedence -----------------------------------------------------------------

    [Fact]
    public void AGroundedUnreservedAircraft_ReportsTheGroundingFirst()
    {
        // Precedence matters, and this is the case that proves it: leading with "reserve it first"
        // on an aircraft that is ALSO in maintenance sends the player to do something that will not
        // help. Every refusal shown must end in an action that actually works.
        var assessment = Evaluate(isGroundedForMaintenance: true, isReservedForPlayer: false);

        Assert.Equal(RepositionRefusal.GroundedForMaintenance, assessment.Refusal);
    }

    [Fact]
    public void AnUnreservedBrokeAircraft_ReportsTheReservationFirst()
    {
        // Cash is checked last because it is the most transient blocker - telling a player to go and
        // earn 2,000 for a move that would be refused on ownership grounds anyway wastes their time.
        var assessment = Evaluate(isReservedForPlayer: false, cashBalance: 0m);

        Assert.Equal(RepositionRefusal.NotReservedForPlayer, assessment.Refusal);
    }

    [Fact]
    public void AnInFlightAircraft_OutranksEverythingElse()
    {
        var assessment = Evaluate(
            isInFlight: true,
            isGroundedForMaintenance: true,
            isReservedForPlayer: false,
            destinations: Array.Empty<string>(),
            airlineHasRoutes: false,
            cashBalance: 0m);

        Assert.Equal(RepositionRefusal.InFlight, assessment.Refusal);
    }

    // ----- config -----------------------------------------------------------------------------

    [Fact]
    public void BothPlaystyles_ChargeTheSameTwoThousand()
    {
        // Shared across playstyles by design - see AircraftRepositioningConfig's own doc. Asserted
        // for both so a future per-playstyle override block is a deliberate change, not a silent one.
        var catalog = EconomyConfigCatalog.Default();

        Assert.Equal(2_000m, catalog.Get(AirlinePlaystyle.Casual).AircraftRepositioning.Cost);
        Assert.Equal(2_000m, catalog.Get(AirlinePlaystyle.TrueLife).AircraftRepositioning.Cost);
    }

    [Fact]
    public void AFreeReposition_IsRejectedByConfigValidation()
    {
        // Repositioning must never be free: at zero, aircraft placement stops meaning anything -
        // park anything anywhere, at will, forever. A config block that was simply forgotten
        // resolves to exactly 0, so this has to fail loudly at load rather than ship a free move.
        // Driven through the real JSON path (round-tripping the shipped defaults and patching the
        // one figure) rather than a hand-built config, because every check before this one in
        // Validate() would otherwise have to be satisfied by hand just to reach it.
        var json = JsonNode.Parse(JsonSerializer.Serialize(EconomyConfig.Default()))!;
        json["AircraftRepositioning"]!["Cost"] = 0m;

        var error = Assert.Throws<InvalidOperationException>(() => EconomyConfig.FromJson(json.ToJsonString()));
        Assert.Contains("must be positive", error.Message);
    }

    [Fact]
    public void ARepositioningCostRoundTripsThroughJson_Unchanged()
    {
        // Guards the wiring rather than the number: a property missing from the JSON shape would
        // silently fall back to the C# default and look correct in every other test here.
        var json = JsonNode.Parse(JsonSerializer.Serialize(EconomyConfig.Default()))!;
        json["AircraftRepositioning"]!["Cost"] = 3_500m;

        Assert.Equal(3_500m, EconomyConfig.FromJson(json.ToJsonString()).AircraftRepositioning.Cost);
    }

    [Fact]
    public void TheRepositioningFee_IsAVariableOperatingCost()
    {
        // It buys nothing the airline still owns afterwards, so it belongs on the variable side of
        // the Finances page's fixed/variable split - never capital, like an aircraft purchase.
        Assert.True(LedgerCostClassifier.IsVariableCost(LedgerCategory.AircraftRepositioning));
        Assert.False(LedgerCostClassifier.IsFixedCost(LedgerCategory.AircraftRepositioning));
        Assert.False(LedgerCostClassifier.IsCapitalOrRevenue(LedgerCategory.AircraftRepositioning));
    }
}
