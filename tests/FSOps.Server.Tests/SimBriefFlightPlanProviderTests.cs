using System.Net;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;

namespace FSOps.Server.Tests;

/// <summary>
/// Every failure path SimBriefFlightPlanProvider must handle as a normal outcome rather than an
/// exception - no Pilot ID, an unknown Pilot ID, a timeout, a network failure, malformed XML, and
/// an OFP filed for a different city pair - plus the happy path's field extraction and unit
/// conversion. Always against FakeHttpMessageHandler; the real simbrief.com is never contacted
/// from this suite (docs/PLAN.md's etiquette rule toward third-party infrastructure, mirrored from
/// VatsimNetworkClientTests).
/// </summary>
public class SimBriefFlightPlanProviderTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    private static readonly Airport Bristol = new()
    {
        Icao = "EGGD", Name = "Bristol Airport", Country = "GB",
        Latitude = 51.3827, Longitude = -2.7191, LongestRunwayFt = 8000,
    };

    private static readonly Airport Edinburgh = new()
    {
        Icao = "EGPH", Name = "Edinburgh Airport", Country = "GB",
        Latitude = 55.9500, Longitude = -3.3725, LongestRunwayFt = 8500,
    };

    private static readonly AircraftType A320 = new()
    {
        Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Name = "A320neo",
        RangeNm = 3400, CruiseTasKts = 450, FuelBurnKgPerHour = 2400,
        MinRunwayFt = 5500, ServiceCeilingFt = 39000,
    };

    private static FlightPlanRequest RequestWithPilotId(string? pilotId = "123456") =>
        new(Bristol, Edinburgh, A320, Config, AirlineStrategyProfile.Domestic, pilotId);

    private static string ValidOfpXml(string units = "kgs", double rampFuel = 5000, string origin = "EGGD", string destination = "EGPH") => $"""
        <OFP>
          <fetch>
            <status>Success</status>
          </fetch>
          <params>
            <units>{units}</units>
          </params>
          <origin>
            <icao_code>{origin}</icao_code>
          </origin>
          <destination>
            <icao_code>{destination}</icao_code>
          </destination>
          <general>
            <route>DCT WELIN DCT</route>
            <initial_altitude>36000</initial_altitude>
          </general>
          <times>
            <est_time_enroute>5400</est_time_enroute>
          </times>
          <fuel>
            <plan_ramp>{rampFuel}</plan_ramp>
          </fuel>
        </OFP>
        """;

    [Fact]
    public async Task GetPlanAsync_NoPilotIdConfigured_FailsWithoutCallingSimBrief()
    {
        var handler = FakeHttpMessageHandler.WithRawBody(ValidOfpXml());
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(null), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("SimBrief", outcome.ProviderName);
        Assert.Null(outcome.Plan);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)] // SimBrief's real response for an unrecognised/planless Pilot ID.
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetPlanAsync_NonSuccessStatus_FallsBackWithoutThrowing(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.WithStatus(status);
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Plan);
        Assert.NotNull(outcome.Message);
    }

    [Fact]
    public async Task GetPlanAsync_Timeout_FallsBackWithoutThrowing()
    {
        var handler = FakeHttpMessageHandler.ThatThrows(new TaskCanceledException("simulated timeout"));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("time", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPlanAsync_NetworkFailure_FallsBackWithoutThrowing()
    {
        var handler = FakeHttpMessageHandler.ThatThrows(new HttpRequestException("simulated network failure"));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Message);
    }

    [Fact]
    public async Task GetPlanAsync_MalformedXml_FallsBackWithoutThrowing()
    {
        var handler = FakeHttpMessageHandler.WithRawBody("<OFP><fetch><status>Success</status>");
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public async Task GetPlanAsync_FetchStatusNotSuccess_FallsBack()
    {
        var xml = """
            <OFP>
              <fetch>
                <status>Error: Unknown UserID</status>
              </fetch>
            </OFP>
            """;
        var handler = FakeHttpMessageHandler.WithRawBody(xml);
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task GetPlanAsync_OfpForDifferentCityPair_RefusesRatherThanSubstituting()
    {
        // A real, well-formed, successful OFP - just filed for a different route than the one the
        // player is actually about to fly (EGLL-EGPF instead of EGGD-EGPH).
        var handler = FakeHttpMessageHandler.WithRawBody(ValidOfpXml(origin: "EGLL", destination: "EGPF"));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Plan);
        Assert.Contains("EGLL", outcome.Message);
        Assert.Contains("EGGD", outcome.Message);
    }

    [Fact]
    public async Task GetPlanAsync_ValidMatchingOfp_ExtractsFuelAltitudeTimeAndRoute()
    {
        var handler = FakeHttpMessageHandler.WithRawBody(ValidOfpXml(units: "kgs", rampFuel: 5000));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("SimBrief", outcome.ProviderName);
        Assert.NotNull(outcome.Plan);
        Assert.Equal(5000, outcome.Plan!.BlockFuelKg);
        Assert.Equal(36000, outcome.Plan.CruiseAltitudeFt);
        Assert.Equal(90, outcome.Plan.BlockTimeMinutes); // 5400s
        Assert.Equal("DCT WELIN DCT", outcome.Plan.RouteString);
    }

    [Fact]
    public async Task GetPlanAsync_FuelInPounds_ConvertsToKg()
    {
        var handler = FakeHttpMessageHandler.WithRawBody(ValidOfpXml(units: "lbs", rampFuel: 11023));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Plan);
        // 11023 lb ~= 5000 kg.
        Assert.InRange(outcome.Plan!.BlockFuelKg, 4999, 5001);
    }

    [Fact]
    public async Task GetPlanAsync_PilotIdIsNeverPlacedInMessageOrThrown()
    {
        var handler = FakeHttpMessageHandler.ThatThrows(new HttpRequestException("boom"));
        var provider = new SimBriefFlightPlanProvider(new HttpClient(handler, disposeHandler: false));
        const string pilotId = "999999";

        var outcome = await provider.GetPlanAsync(RequestWithPilotId(pilotId), CancellationToken.None);

        Assert.DoesNotContain(pilotId, outcome.Message);
    }
}
