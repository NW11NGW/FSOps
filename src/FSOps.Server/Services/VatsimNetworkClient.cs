using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Core.Time;

namespace FSOps.Server.Services;

/// <summary>
/// One controller currently logged on to the VATSIM network, trimmed down to what the dashboard's
/// ATC layer needs to answer "is anyone controlling where I am flying right now" - see
/// VATSIM's public, keyless JSON feed. The raw feed's much larger controller/pilot payload is
/// parsed and discarded here; nothing beyond this shape is retained, and pilot entries are never
/// read at all.
/// </summary>
public sealed record VatsimController(
    string Callsign,
    int Cid,
    string Name,
    string Frequency,
    int FacilityId,
    double? VisualRangeNm,
    DateTimeOffset LogonTimeUtc);

/// <summary>
/// One pilot currently connected to VATSIM, trimmed down to what the two pilot-facing features need
/// it for: G8 (VatsimFlightCorroborationService, matching a tracked flight's own CID against its
/// reported position) and G11 (other VATSIM traffic on the live operations map). Unlike
/// <see cref="VatsimController"/>, this WAS the data VatsimNetworkClient's class doc used to say
/// "pilot entries are never read at all" - that changed the moment corroborating a flight against
/// the network became a feature, not a hypothetical. Still trimmed hard: nothing beyond this shape
/// is retained, and nothing about it is ever sent anywhere but back to the FSOps user who is already
/// looking at their own dashboard or report card.
/// </summary>
public sealed record VatsimPilot(
    string Callsign,
    int Cid,
    string Name,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeFt,
    double GroundSpeedKt,
    double HeadingDeg,
    string? DepartureIcao,
    string? ArrivalIcao,
    DateTimeOffset LogonTimeUtc,
    DateTimeOffset LastUpdatedUtc);

/// <summary>
/// Result of the most recent attempt to read the VATSIM feed. <see cref="Available"/> is false
/// when the last fetch failed (network error, timeout, non-success status, or a payload that
/// didn't parse) - callers must treat that as an ordinary, expected outcome and degrade the ATC
/// layer to "unavailable" rather than letting an exception reach the endpoint.
/// </summary>
public sealed record VatsimSnapshot(
    bool Available, DateTimeOffset? FetchedAtUtc, IReadOnlyList<VatsimController> Controllers, IReadOnlyList<VatsimPilot> Pilots);

public interface IVatsimNetworkClient
{
    Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct);
}

/// <summary>
/// Fetches and caches VATSIM's public status feed (vatsim-data.json - no API key, documented at
/// vatsim.dev, regenerates roughly every 15 seconds). Registered as a singleton so every request
/// for /operations/atc, from every connected client, shares one cache and one in-flight fetch
/// rather than each poll or each browser tab hitting VATSIM's infrastructure independently - see
/// this is etiquette toward third-party infrastructure FSOps does not pay for: poll no more often
/// than the 15-second regeneration interval, cache between polls, share one fetch across all
/// features, and back off on errors.
///
/// A failed fetch (timeout, non-success status, malformed JSON) is cached exactly like a
/// successful one: as an "unavailable" snapshot stamped with the attempt time. That is what makes
/// this a backoff rather than a retry storm - the next caller within <see cref="RefreshInterval"/>
/// gets the same cached failure instead of triggering another request, so VATSIM being down never
/// costs more than one failed request per refresh interval no matter how many clients are polling.
/// This is presentation data only: nothing here is persisted, and nothing about a controller being
/// online ever reaches the economy, reputation or ledger.
/// </summary>
public sealed class VatsimNetworkClient : IVatsimNetworkClient
{
    private const string FeedUrl = "https://data.vatsim.net/v3/vatsim-data.json";
    private const string HttpClientName = "vatsim";

    // Observers aren't providing a service to anyone - facility 0 in the feed. Nothing about
    // "is a controller online" should be true for someone who is just watching the network.
    private const int ObserverFacilityId = 0;

    // VATSIM regenerates the feed roughly every 15 seconds. A margin above
    // that keeps this client from ever being the one polling faster than the feed actually changes.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly ILogger<VatsimNetworkClient> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile VatsimSnapshot? _cache;

    public VatsimNetworkClient(IHttpClientFactory httpClientFactory, IClock clock, ILogger<VatsimNetworkClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var cached = _cache;
        if (cached is not null && _clock.UtcNow - cached.FetchedAtUtc < RefreshInterval)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock - another caller may have just refreshed while
            // this one was waiting, which is exactly the "share one fetch" behaviour this lock
            // exists for.
            cached = _cache;
            if (cached is not null && _clock.UtcNow - cached.FetchedAtUtc < RefreshInterval)
            {
                return cached;
            }

            var snapshot = await FetchAsync(ct);
            _cache = snapshot;
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<VatsimSnapshot> FetchAsync(CancellationToken ct)
    {
        var attemptedAt = _clock.UtcNow;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            var feed = await client.GetFromJsonAsync<VatsimFeedRoot>(FeedUrl, JsonOptions, timeoutCts.Token);
            var controllers = (feed?.Controllers ?? new List<VatsimFeedController>())
                .Where(c => c.Facility != ObserverFacilityId && !string.IsNullOrWhiteSpace(c.Callsign))
                .Select(c => new VatsimController(
                    c.Callsign,
                    c.Cid,
                    c.Name ?? string.Empty,
                    c.Frequency ?? string.Empty,
                    c.Facility,
                    c.VisualRange,
                    c.LogonTime))
                .ToList();

            // last_updated falls back to the fetch time itself for a payload that omits it (an
            // older/different feed shape) - "just seen" is a safer default than treating a missing
            // timestamp as infinitely stale, which would silently fail every corroboration check.
            var pilots = (feed?.Pilots ?? new List<VatsimFeedPilot>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Callsign))
                .Select(p => new VatsimPilot(
                    p.Callsign,
                    p.Cid,
                    p.Name ?? string.Empty,
                    p.Latitude,
                    p.Longitude,
                    p.Altitude,
                    p.GroundSpeed,
                    p.Heading,
                    NormaliseIcao(p.FlightPlan?.Departure),
                    NormaliseIcao(p.FlightPlan?.Arrival),
                    p.LogonTime,
                    p.LastUpdated ?? attemptedAt))
                .ToList();

            return new VatsimSnapshot(true, attemptedAt, controllers, pilots);
        }
        // Only swallow a cancellation that this method caused itself (the request timeout above);
        // a cancellation the caller asked for must propagate like any other cancelled request.
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or NotSupportedException
            or TaskCanceledException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "VATSIM feed fetch failed - showing the ATC layer as unavailable");
            return new VatsimSnapshot(false, attemptedAt, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>());
        }
    }

    /// <summary>Blank filed-plan fields ("", whitespace) read as "not filed" rather than an empty
    /// ICAO, which would otherwise falsely match a candidate-ICAO lookup for an empty string.</summary>
    private static string? NormaliseIcao(string? icao) => string.IsNullOrWhiteSpace(icao) ? null : icao;

    private sealed record VatsimFeedRoot(
        [property: JsonPropertyName("controllers")] List<VatsimFeedController>? Controllers,
        [property: JsonPropertyName("pilots")] List<VatsimFeedPilot>? Pilots);

    private sealed record VatsimFeedController(
        [property: JsonPropertyName("cid")] int Cid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("callsign")] string Callsign,
        [property: JsonPropertyName("frequency")] string? Frequency,
        [property: JsonPropertyName("facility")] int Facility,
        [property: JsonPropertyName("visual_range")] double? VisualRange,
        [property: JsonPropertyName("logon_time")] DateTimeOffset LogonTime);

    private sealed record VatsimFeedPilot(
        [property: JsonPropertyName("cid")] int Cid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("callsign")] string Callsign,
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude,
        [property: JsonPropertyName("altitude")] double Altitude,
        [property: JsonPropertyName("groundspeed")] double GroundSpeed,
        [property: JsonPropertyName("heading")] double Heading,
        [property: JsonPropertyName("logon_time")] DateTimeOffset LogonTime,
        [property: JsonPropertyName("last_updated")] DateTimeOffset? LastUpdated,
        [property: JsonPropertyName("flight_plan")] VatsimFeedFlightPlan? FlightPlan);

    private sealed record VatsimFeedFlightPlan(
        [property: JsonPropertyName("departure")] string? Departure,
        [property: JsonPropertyName("arrival")] string? Arrival);
}
