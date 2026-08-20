using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.SimAircraft;

/// <summary>
/// The precedence rules that decide whether a contract may name an aircraft. These are the rules
/// that make the difference between a contract board somebody can fly and one full of aircraft they
/// do not own.
/// </summary>
public class ContractAircraftAvailabilityResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A brand-new player with nothing stored: no scan, no ticks, and Standard because that is what
    /// a UserSettings row defaults to. They get the base aircraft and nothing else.
    /// </summary>
    [Fact]
    public void ABrandNewPlayerWithNothingStored_GetsExactlyTheStandardEditionAircraft()
    {
        var resolved = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, scan: null, overrides: null);

        Assert.All(resolved, r => Assert.Equal(
            r.Aircraft.ShipsWith == SimAircraftAvailability.Standard,
            r.Available));

        Assert.All(
            resolved.Where(r => r.Available),
            r => Assert.Equal(AircraftAvailabilityEvidence.Edition, r.Evidence));

        // The concrete claim, in the two directions that matter.
        Assert.True(Available(resolved, "C172"));
        Assert.False(Available(resolved, "A320"));
        Assert.False(Available(resolved, "SF34"));
    }

    [Fact]
    public void EditionsAreCumulative()
    {
        var standard = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, null, null);
        var deluxe = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Deluxe, null, null);
        var premium = ContractAircraftAvailabilityResolver.Resolve(SimEdition.PremiumDeluxe, null, null);

        Assert.False(Available(standard, "C408"));
        Assert.True(Available(deluxe, "C408"));
        Assert.True(Available(premium, "C408"));

        Assert.False(Available(deluxe, "SF34"));
        Assert.True(Available(premium, "SF34"));

        // Nothing an edition adds is ever taken away by a bigger one.
        var standardAvailable = standard.Where(r => r.Available).Select(r => r.Aircraft.TypeDesignator);
        Assert.All(standardAvailable, d => Assert.True(Available(premium, d)));
    }

    /// <summary>No edition, however expensive, ever includes an add-on.</summary>
    [Fact]
    public void NoEditionEverIncludesAnAddOn()
    {
        var premium = ContractAircraftAvailabilityResolver.Resolve(SimEdition.PremiumDeluxe, null, null);

        Assert.All(
            premium.Where(r => r.Aircraft.ShipsWith == SimAircraftAvailability.AddOn),
            r => Assert.False(r.Available));
    }

    [Fact]
    public void AnAddOnFoundInTheCommunityFolderBecomesAvailable()
    {
        var scan = ScanFinding(community: new[] { "A320", "A359" }, onDisk: Array.Empty<string>());

        var resolved = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, scan, null);

        Assert.True(Available(resolved, "A320"));
        Assert.Equal(AircraftAvailabilityEvidence.CommunityFolder, Evidence(resolved, "A320"));
        Assert.True(Available(resolved, "A359"));
        Assert.False(Available(resolved, "A388"));
    }

    [Fact]
    public void BaseContentFoundOnDiskIsEvidenceInItsOwnRight()
    {
        var scan = ScanFinding(community: Array.Empty<string>(), onDisk: new[] { "C408" });

        var resolved = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, scan, null);

        Assert.True(Available(resolved, "C408"));
        Assert.Equal(AircraftAvailabilityEvidence.InstalledOnDisk, Evidence(resolved, "C408"));
    }

    /// <summary>
    /// <b>The rule this whole feature stands on.</b> MSFS 2024 streams most base content and only
    /// keeps on disk what has actually been used, so a scan can prove an aircraft is present and can
    /// never prove one is absent. A scan that found nothing must therefore take nothing away - or
    /// every player who has not yet flown a 172 would lose their 172.
    /// </summary>
    [Fact]
    public void AScanThatFindsNothingNeverRemovesAnythingTheEditionIncludes()
    {
        var empty = ScanFinding(Array.Empty<string>(), Array.Empty<string>());
        var withoutScan = ContractAircraftAvailabilityResolver.AvailableDesignators(SimEdition.Deluxe, null, null);
        var withEmptyScan = ContractAircraftAvailabilityResolver.AvailableDesignators(SimEdition.Deluxe, empty, null);

        Assert.Equal(withoutScan, withEmptyScan);
        Assert.Contains("C172", withEmptyScan);
    }

    /// <summary>
    /// Failed scans are the ones most likely to be sitting in the settings row - a moved sim, a
    /// path typed wrong - and none of them may cost the player an aircraft.
    /// </summary>
    [Theory]
    [InlineData(AircraftScanOutcome.NoFolder)]
    [InlineData(AircraftScanOutcome.FolderMissing)]
    [InlineData(AircraftScanOutcome.NotAPackagesFolder)]
    public void AFailedScanNeverRemovesAnything(AircraftScanOutcome outcome)
    {
        var failed = AircraftScanResult.NotRun(outcome, @"D:\wherever", Now);

        Assert.Equal(
            ContractAircraftAvailabilityResolver.AvailableDesignators(SimEdition.Standard, null, null),
            ContractAircraftAvailabilityResolver.AvailableDesignators(SimEdition.Standard, failed, null));
    }

    [Fact]
    public void APlayersOwnTickBeatsEverythingElse()
    {
        var scan = ScanFinding(community: new[] { "A320" }, onDisk: new[] { "C172" });
        var overrides = new ContractAircraftOverrides(On: new[] { "AT72" }, Off: new[] { "A320", "C172" });

        var resolved = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, scan, overrides);

        Assert.True(Available(resolved, "AT72"));
        Assert.Equal(AircraftAvailabilityEvidence.TickedOn, Evidence(resolved, "AT72"));

        // Both of these were found on disk, and the player says otherwise. They win.
        Assert.False(Available(resolved, "A320"));
        Assert.Equal(AircraftAvailabilityEvidence.TickedOff, Evidence(resolved, "A320"));
        Assert.False(Available(resolved, "C172"));
    }

    [Fact]
    public void OverridesAreMatchedCaseInsensitively()
    {
        var overrides = new ContractAircraftOverrides(On: new[] { "at72" }, Off: new[] { "c172" });

        var resolved = ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, null, overrides);

        Assert.True(Available(resolved, "AT72"));
        Assert.False(Available(resolved, "C172"));
    }

    [Fact]
    public void ResolveAlwaysReturnsTheWholeCatalogueSoTheTickListCanShowEverything()
    {
        Assert.Equal(
            ContractAircraftCatalogue.All.Count,
            ContractAircraftAvailabilityResolver.Resolve(SimEdition.Standard, null, null).Count);
    }

    private static AircraftScanResult ScanFinding(string[] community, string[] onDisk) =>
        new(
            AircraftScanOutcome.Scanned,
            @"D:\MSFS\Community",
            Now,
            community.Length,
            community.Select(d => new ScannedAircraftPackage($"pkg-{d}", $"Package {d}", d, d)).ToList(),
            onDisk);

    private static bool Available(IEnumerable<ResolvedContractAircraft> resolved, string designator) =>
        resolved.Single(r => r.Aircraft.TypeDesignator == designator).Available;

    private static AircraftAvailabilityEvidence Evidence(IEnumerable<ResolvedContractAircraft> resolved, string designator) =>
        resolved.Single(r => r.Aircraft.TypeDesignator == designator).Evidence;
}
