using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The service that decides which aircraft a contract may be written for: reading and writing the
/// settings row, caching a scan, and honouring the player's own ticks.
/// </summary>
public class SimAircraftServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fsops-simaircraft-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnection _connection;
    private readonly FsOpsDbContext _db;
    private readonly SimAircraftService _service;
    private readonly Guid _user = Guid.NewGuid();

    public SimAircraftServiceTests()
    {
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection($"Data Source=file:{Guid.NewGuid():N}?Mode=Memory;Cache=Shared");
        _connection.Open();
        _db = new FsOpsDbContext(new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _service = new SimAircraftService(
            _db,
            new InstalledAircraftScanner(),
            new FakeClock(Now),
            NullLogger<SimAircraftService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }

    /// <summary>
    /// Somebody who has just installed FSOps and never opened this screen. Standard edition, no
    /// scan, nothing ticked - and a settings row created for them without an explicit setup step.
    /// </summary>
    [Fact]
    public async Task ABrandNewPlayerWithNothingStored_IsOnStandardWithNoScan()
    {
        var state = await _service.GetAsync(_user, CancellationToken.None);

        Assert.Equal(SimEdition.Standard, state.Edition);
        Assert.Null(state.LastScan);
        Assert.Null(state.ConfiguredCommunityFolderPath);
        Assert.Equal(ContractAircraftCatalogue.All.Count, state.Aircraft.Count);

        Assert.All(state.Aircraft, a => Assert.Equal(
            a.Aircraft.ShipsWith == SimAircraftAvailability.Standard,
            a.Available));

        var stored = await _db.UserSettings.SingleAsync(s => s.OwnerUserId == _user);
        Assert.Equal(SimEdition.Standard, stored.SimEdition);
        Assert.Null(stored.SimAircraftScanJson);
        Assert.Null(stored.SimAircraftOverridesJson);
    }

    [Fact]
    public async Task UpdateAsync_StoresTheEditionAndTheFolder()
    {
        var state = await _service.UpdateAsync(_user, SimEdition.PremiumDeluxe, @"D:\MSFS\Community", false, CancellationToken.None);

        Assert.Equal(SimEdition.PremiumDeluxe, state.Edition);
        Assert.Equal(@"D:\MSFS\Community", state.ConfiguredCommunityFolderPath);
        Assert.Equal(@"D:\MSFS\Community", state.EffectiveCommunityFolderPath);

        var stored = await _db.UserSettings.SingleAsync(s => s.OwnerUserId == _user);
        Assert.Equal(SimEdition.PremiumDeluxe, stored.SimEdition);
        Assert.Equal(@"D:\MSFS\Community", stored.CommunityFolderPath);
    }

    /// <summary>
    /// "Leave this alone" and "forget this" are different requests, and a null path cannot mean
    /// both - so clearing is its own flag. Getting this wrong would silently wipe a path somebody
    /// typed in every time they changed their edition.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_LeavesAStoredFolderAloneUnlessAskedToClearIt()
    {
        await _service.UpdateAsync(_user, SimEdition.Standard, @"D:\MSFS\Community", false, CancellationToken.None);

        var unchanged = await _service.UpdateAsync(_user, SimEdition.Deluxe, null, false, CancellationToken.None);
        Assert.Equal(@"D:\MSFS\Community", unchanged.ConfiguredCommunityFolderPath);

        var cleared = await _service.UpdateAsync(_user, SimEdition.Deluxe, null, true, CancellationToken.None);
        Assert.Null(cleared.ConfiguredCommunityFolderPath);
    }

    [Fact]
    public async Task ScanAsync_FindsAnAddOnAndMakesItAvailable()
    {
        var community = CreateCommunityWithFenixA320();
        await _service.UpdateAsync(_user, SimEdition.Standard, community, false, CancellationToken.None);

        var state = await _service.ScanAsync(_user, CancellationToken.None);

        Assert.NotNull(state.LastScan);
        Assert.Equal(AircraftScanOutcome.Scanned, state.LastScan!.Outcome);
        Assert.Equal(Now, state.LastScan.ScannedUtc);
        Assert.Equal(new[] { "A320" }, state.LastScan.IdentifiedTypeDesignators);

        Assert.True(Available(state, "A320"));
        Assert.Equal(AircraftAvailabilityEvidence.CommunityFolder, Evidence(state, "A320"));
    }

    /// <summary>The scan is cached, so contract generation does not walk the disk on every request.</summary>
    [Fact]
    public async Task ScanAsync_StoresTheResultSoALaterReadDoesNotHaveToScanAgain()
    {
        var community = CreateCommunityWithFenixA320();
        await _service.UpdateAsync(_user, SimEdition.Standard, community, false, CancellationToken.None);
        await _service.ScanAsync(_user, CancellationToken.None);

        // Deleting the folder proves the second read is not touching the disk.
        Directory.Delete(community, recursive: true);

        var state = await _service.GetAsync(_user, CancellationToken.None);

        Assert.Equal(AircraftScanOutcome.Scanned, state.LastScan!.Outcome);
        Assert.True(Available(state, "A320"));
    }

    /// <summary>
    /// A folder that is not there must come back saying so, and - the part that matters - must not
    /// cost the player any of the aircraft their edition includes.
    /// </summary>
    [Fact]
    public async Task ScanAsync_WithAFolderThatIsNotThere_ReportsItAndTakesNothingAway()
    {
        await _service.UpdateAsync(_user, SimEdition.Standard, Path.Combine(_root, "gone"), false, CancellationToken.None);

        var state = await _service.ScanAsync(_user, CancellationToken.None);

        Assert.Equal(AircraftScanOutcome.FolderMissing, state.LastScan!.Outcome);
        Assert.True(Available(state, "C172"));
        Assert.Equal(AircraftAvailabilityEvidence.Edition, Evidence(state, "C172"));
    }

    [Fact]
    public async Task ScanAsync_WithAFolderThatIsNotACommunityFolder_SaysSo()
    {
        var wrong = Path.Combine(_root, "FlightSimulator");
        Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "FlightSimulator2024.exe"), "not a package");

        await _service.UpdateAsync(_user, SimEdition.Standard, wrong, false, CancellationToken.None);
        var state = await _service.ScanAsync(_user, CancellationToken.None);

        Assert.Equal(AircraftScanOutcome.NotAPackagesFolder, state.LastScan!.Outcome);
        Assert.True(Available(state, "C172"));
    }

    [Fact]
    public async Task SetOverrideAsync_TicksAnAircraftOnAndOffAndClearsAgain()
    {
        var ticked = await _service.SetOverrideAsync(_user, "AT72", true, CancellationToken.None);
        Assert.True(Available(ticked, "AT72"));
        Assert.Equal(AircraftAvailabilityEvidence.TickedOn, Evidence(ticked, "AT72"));

        var unticked = await _service.SetOverrideAsync(_user, "C172", false, CancellationToken.None);
        Assert.False(Available(unticked, "C172"));
        Assert.Equal(AircraftAvailabilityEvidence.TickedOff, Evidence(unticked, "C172"));

        var cleared = await _service.SetOverrideAsync(_user, "C172", null, CancellationToken.None);
        Assert.True(Available(cleared, "C172"));
        Assert.Equal(AircraftAvailabilityEvidence.Edition, Evidence(cleared, "C172"));
    }

    [Fact]
    public async Task SetOverrideAsync_IsCaseInsensitiveAboutTheDesignator()
    {
        var state = await _service.SetOverrideAsync(_user, "at72", true, CancellationToken.None);
        Assert.True(Available(state, "AT72"));
    }

    [Fact]
    public async Task SetOverrideAsync_RejectsAnAircraftTheCatalogueDoesNotHave()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetOverrideAsync(_user, "NOT-A-PLANE", true, CancellationToken.None));
    }

    /// <summary>
    /// A tick that agrees with FSOps is not stored. Otherwise somebody who ticks the ATR on today,
    /// and installs it tomorrow, carries a stale override that outranks the scan for ever - and if
    /// they later uninstall it, the stale tick would go on claiming they have it.
    /// </summary>
    [Fact]
    public async Task SetOverrideAsync_DoesNotStoreATickThatAgreesWithWhatFsOpsAlreadyWorkedOut()
    {
        await _service.SetOverrideAsync(_user, "C172", true, CancellationToken.None);

        var stored = await _db.UserSettings.SingleAsync(s => s.OwnerUserId == _user);
        Assert.Null(stored.SimAircraftOverridesJson);
    }

    /// <summary>
    /// A settings row whose stored JSON has been corrupted must degrade to "not scanned", not take
    /// the settings page down. The worst it can cost is a scan the player re-runs with a button.
    /// </summary>
    [Fact]
    public async Task UnreadableStoredJson_IsTreatedAsNotSetRatherThanThrowing()
    {
        await _service.GetAsync(_user, CancellationToken.None);
        var settings = await _db.UserSettings.SingleAsync(s => s.OwnerUserId == _user);
        settings.SimAircraftScanJson = "{ not json";
        settings.SimAircraftOverridesJson = "also not json";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var state = await _service.GetAsync(_user, CancellationToken.None);

        Assert.Null(state.LastScan);
        Assert.True(Available(state, "C172"));
    }

    /// <summary>The stored scan survives a round trip through JSON with every field intact.</summary>
    [Fact]
    public async Task AStoredScanRoundTripsThroughJsonWithoutLosingAnything()
    {
        var community = CreateCommunityWithFenixA320();
        await _service.UpdateAsync(_user, SimEdition.Standard, community, false, CancellationToken.None);
        var scanned = await _service.ScanAsync(_user, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var read = await _service.GetAsync(_user, CancellationToken.None);

        Assert.Equal(scanned.LastScan!.Outcome, read.LastScan!.Outcome);
        Assert.Equal(scanned.LastScan.ScannedUtc, read.LastScan.ScannedUtc);
        Assert.Equal(scanned.LastScan.CommunityFolderPath, read.LastScan.CommunityFolderPath);
        Assert.Equal(scanned.LastScan.PackagesInspected, read.LastScan.PackagesInspected);
        Assert.Equal(
            scanned.LastScan.AircraftPackages.Select(p => (p.PackageFolder, p.PackageTitle, p.TypeDesignator)),
            read.LastScan.AircraftPackages.Select(p => (p.PackageFolder, p.PackageTitle, p.TypeDesignator)));
    }

    private string CreateCommunityWithFenixA320()
    {
        var community = Path.Combine(_root, "Packages", "Community");
        var package = Path.Combine(community, "fnx-aircraft-320");
        var config = Path.Combine(package, "SimObjects", "Airplanes", "FNX_32X");
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(package, "manifest.json"),
            """{"content_type":"AIRCRAFT","title":"Fenix Airbus A320","creator":"Fenix Simulations"}""");
        File.WriteAllLines(
            Path.Combine(config, "aircraft.cfg"),
            new[] { "[GENERAL]", "icao_type_designator = \"A320\"" });

        return community;
    }

    private static bool Available(SimAircraftState state, string designator) =>
        state.Aircraft.Single(a => a.Aircraft.TypeDesignator == designator).Available;

    private static AircraftAvailabilityEvidence Evidence(SimAircraftState state, string designator) =>
        state.Aircraft.Single(a => a.Aircraft.TypeDesignator == designator).Evidence;
}
