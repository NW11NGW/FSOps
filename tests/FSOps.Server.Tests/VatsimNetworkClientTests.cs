using System.Net;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Exercises VatsimNetworkClient entirely against a stub HttpMessageHandler - the real VATSIM
/// feed is never hit from a test (see docs/PLAN.md's etiquette rule and the project's own ban on
/// polling third-party infrastructure from CI). Covers the caching/backoff contract as well as
/// every failure shape the endpoint must degrade gracefully from: bad status, malformed body, and
/// a fetch that throws outright.
/// </summary>
public class VatsimNetworkClientTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string SampleFeedJson = """
    {
      "controllers": [
        { "cid": 1, "name": "Alice", "callsign": "EGLL_TWR", "frequency": "118.500", "facility": 4, "visual_range": 50, "logon_time": "2026-01-01T00:00:00Z" },
        { "cid": 2, "name": "Bob", "callsign": "LON_CTR", "frequency": "127.100", "facility": 6, "visual_range": 300, "logon_time": "2026-01-01T00:00:00Z" },
        { "cid": 3, "name": "Carl", "callsign": "N0002", "frequency": "199.998", "facility": 0, "visual_range": null, "logon_time": "2026-01-01T00:00:00Z" }
      ]
    }
    """;

    private static VatsimNetworkClient CreateClient(FakeHttpMessageHandler handler, FakeClock clock) =>
        new(new FakeHttpClientFactory(handler), clock, NullLogger<VatsimNetworkClient>.Instance);

    [Fact]
    public async Task GetSnapshotAsync_ParsesControllers_AndFiltersOutObservers()
    {
        var handler = FakeHttpMessageHandler.WithJson(SampleFeedJson);
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Equal(Base, snapshot.FetchedAtUtc);
        Assert.Equal(2, snapshot.Controllers.Count);
        Assert.DoesNotContain(snapshot.Controllers, c => c.FacilityId == 0);
        Assert.Contains(snapshot.Controllers, c => c.Callsign == "EGLL_TWR" && c.FacilityId == 4 && c.VisualRangeNm == 50);
        Assert.Contains(snapshot.Controllers, c => c.Callsign == "LON_CTR" && c.FacilityId == 6);
    }

    [Fact]
    public async Task GetSnapshotAsync_EmptyControllerList_IsAvailableWithNoControllers()
    {
        var handler = FakeHttpMessageHandler.WithJson("""{ "controllers": [] }""");
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Empty(snapshot.Controllers);
    }

    [Fact]
    public async Task GetSnapshotAsync_MissingControllersKey_IsAvailableWithNoControllers()
    {
        // A payload shaped differently than expected (e.g. the feed schema drifts) must degrade
        // to "no controllers", never throw past the endpoint.
        var handler = FakeHttpMessageHandler.WithJson("""{ "general": { "version": 3 } }""");
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Empty(snapshot.Controllers);
    }

    [Fact]
    public async Task GetSnapshotAsync_NonSuccessStatus_ReturnsUnavailable_DoesNotThrow()
    {
        var handler = FakeHttpMessageHandler.WithStatus(HttpStatusCode.ServiceUnavailable);
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Empty(snapshot.Controllers);
        Assert.Equal(Base, snapshot.FetchedAtUtc);
    }

    [Fact]
    public async Task GetSnapshotAsync_MalformedJson_ReturnsUnavailable_DoesNotThrow()
    {
        var handler = FakeHttpMessageHandler.WithRawBody("{ this is not valid json");
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Empty(snapshot.Controllers);
    }

    [Fact]
    public async Task GetSnapshotAsync_FetchThrows_ReturnsUnavailable_DoesNotThrow()
    {
        // Stands in for a real network timeout/DNS failure without a test actually waiting on one.
        var handler = FakeHttpMessageHandler.ThatThrows(new HttpRequestException("simulated network failure"));
        var client = CreateClient(handler, new FakeClock(Base));

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
    }

    [Fact]
    public async Task GetSnapshotAsync_CallerCancellation_PropagatesRatherThanBeingSwallowedAsUnavailable()
    {
        // A client disconnecting must surface as a cancellation like any other request - it is not
        // the same event as VATSIM being unreachable and must not be reported as "unavailable".
        var handler = FakeHttpMessageHandler.WithJson(SampleFeedJson);
        var client = CreateClient(handler, new FakeClock(Base));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSnapshotAsync(cts.Token));
    }

    [Fact]
    public async Task GetSnapshotAsync_WithinRefreshInterval_ServesCachedSnapshot_WithoutRefetching()
    {
        var handler = FakeHttpMessageHandler.WithJson(SampleFeedJson);
        var clock = new FakeClock(Base);
        var client = CreateClient(handler, clock);

        await client.GetSnapshotAsync(CancellationToken.None);
        clock.UtcNow = Base.AddSeconds(5); // well inside the ~20s refresh interval
        var second = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(Base, second.FetchedAtUtc);
    }

    [Fact]
    public async Task GetSnapshotAsync_AfterRefreshInterval_RefetchesAndAdvancesTimestamp()
    {
        var handler = FakeHttpMessageHandler.WithJson(SampleFeedJson);
        var clock = new FakeClock(Base);
        var client = CreateClient(handler, clock);

        await client.GetSnapshotAsync(CancellationToken.None);
        clock.UtcNow = Base.AddSeconds(25); // past the ~20s refresh interval
        var second = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(Base.AddSeconds(25), second.FetchedAtUtc);
    }

    [Fact]
    public async Task GetSnapshotAsync_FailureIsCached_SoRepeatedCallsDoNotRefetchWithinTheInterval()
    {
        // This is the backoff contract: a down feed costs at most one failed request per refresh
        // interval, no matter how many callers ask in between.
        var handler = FakeHttpMessageHandler.WithStatus(HttpStatusCode.InternalServerError);
        var clock = new FakeClock(Base);
        var client = CreateClient(handler, clock);

        await client.GetSnapshotAsync(CancellationToken.None);
        await client.GetSnapshotAsync(CancellationToken.None);
        clock.UtcNow = Base.AddSeconds(5);
        var third = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.False(third.Available);
    }
}
