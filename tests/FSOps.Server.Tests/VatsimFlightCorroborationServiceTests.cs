using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// Drives VatsimFlightCorroborationService.CheckAsync directly against a fake IVatsimNetworkClient
/// - the same fake shape VatsimEndpointsTests already uses for VatsimNetworkClient, so both test
/// classes exercise the shared feed contract the same way. Pure logic: no database, no
/// FlightLifecycleService - see VatsimOnlineCorroborationAndBonusTests for how the result of a
/// check actually gets recorded onto a Flight row.
/// </summary>
public class VatsimFlightCorroborationServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int TowerFacility = 4;
    private const int CenterFacility = 6;

    private sealed class FakeVatsimNetworkClient : IVatsimNetworkClient
    {
        private readonly VatsimSnapshot _snapshot;
        public FakeVatsimNetworkClient(VatsimSnapshot snapshot) => _snapshot = snapshot;
        public Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(_snapshot);
    }

    private static VatsimPilot Pilot(int cid, string callsign, double lat, double lon, string? dep = null, string? arr = null) =>
        new(callsign, cid, "Test Pilot", lat, lon, AltitudeFt: 35000, GroundSpeedKt: 450, HeadingDeg: 90, dep, arr, Base, Base);

    [Fact]
    public async Task CheckAsync_CidOnlineNearReportedPosition_Matches()
    {
        // EGPH is roughly (55.95, -3.3725) - the pilot is reported essentially there.
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[] { Pilot(123456, "BAW123", 55.9500, -3.3725) }));
        var service = new VatsimFlightCorroborationService(client);

        var result = await service.CheckAsync(123456, 55.9500, -3.3725, "EGGD", "EGPH", CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal("BAW123", result.Callsign);
        Assert.NotNull(result.DistanceNm);
        Assert.True(result.DistanceNm < 1.0);
    }

    [Fact]
    public async Task CheckAsync_CidOnlineButFarFromReportedPosition_DoesNotMatch()
    {
        // The pilot's CID is online, but reported over Paris (LFPG) while FSOps' own telemetry
        // says the flight is over Edinburgh - too far apart to be the same flight, even though the
        // CID genuinely is connected to the network right now.
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[] { Pilot(123456, "BAW123", 48.9, 2.55) }));
        var service = new VatsimFlightCorroborationService(client);

        var result = await service.CheckAsync(123456, 55.9500, -3.3725, "EGGD", "EGPH", CancellationToken.None);

        Assert.False(result.Matched);
        // Still reports the callsign it saw, even on a miss - the caller may want to know what the
        // CID was flying under even when the position didn't corroborate this particular sample.
        Assert.Equal("BAW123", result.Callsign);
        Assert.True(result.DistanceNm > 100);
    }

    [Fact]
    public async Task CheckAsync_CidNotOnline_ReturnsNoMatchAndNoCallsign()
    {
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[] { Pilot(999999, "OTHER1", 55.95, -3.37) }));
        var service = new VatsimFlightCorroborationService(client);

        var result = await service.CheckAsync(123456, 55.9500, -3.3725, "EGGD", "EGPH", CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Null(result.Callsign);
        Assert.Null(result.DistanceNm);
    }

    [Fact]
    public async Task CheckAsync_FeedUnavailable_FailsSoftWithNoMatch()
    {
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            false, Base, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>()));
        var service = new VatsimFlightCorroborationService(client);

        var result = await service.CheckAsync(123456, 55.9500, -3.3725, "EGGD", "EGPH", CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Null(result.Callsign);
        Assert.Empty(result.RelevantControllers);
    }

    [Fact]
    public async Task CheckAsync_ReturnsControllersAtDepartureAndArrivalOnly()
    {
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, new[]
            {
                new VatsimController("EGGD_TWR", 1, "Alice", "118.0", TowerFacility, 30, Base), // departure - relevant
                new VatsimController("EGPH_GND", 2, "Bob", "121.7", TowerFacility, 20, Base), // arrival - relevant
                new VatsimController("EGLL_TWR", 3, "Carl", "118.5", TowerFacility, 30, Base), // unrelated airport
                new VatsimController("LON_CTR", 4, "Dave", "129.4", CenterFacility, null, Base), // en-route sector, not airport-local
            },
            new[] { Pilot(123456, "BAW123", 55.9500, -3.3725) }));
        var service = new VatsimFlightCorroborationService(client);

        var result = await service.CheckAsync(123456, 55.9500, -3.3725, "EGGD", "EGPH", CancellationToken.None);

        Assert.Equal(new[] { "EGGD_TWR", "EGPH_GND" }, result.RelevantControllers.OrderBy(c => c, StringComparer.Ordinal));
    }
}
