using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class BuiltInFlightPlanProviderTests
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

    [Fact]
    public async Task GetPlanAsync_AlwaysSucceeds_AndMatchesRoutePreviewCalculator()
    {
        var provider = new BuiltInFlightPlanProvider();
        var request = new FlightPlanRequest(Bristol, Edinburgh, A320, Config, AirlineStrategyProfile.Domestic, SimBriefPilotId: null);

        var outcome = await provider.GetPlanAsync(request, CancellationToken.None);

        var expected = RoutePreviewCalculator.Calculate(Config, Bristol, Edinburgh, A320, AirlineStrategyProfile.Domestic);
        Assert.True(outcome.Success);
        Assert.Equal("FSOps", outcome.ProviderName);
        Assert.NotNull(outcome.Plan);
        Assert.Equal(expected.FuelBreakdown.TotalFuelKg, outcome.Plan!.BlockFuelKg);
        Assert.Equal(expected.CruiseAltitudeFt, outcome.Plan.CruiseAltitudeFt);
        Assert.Equal(expected.BlockTimeBreakdown.TotalMinutes, outcome.Plan.BlockTimeMinutes);
        Assert.Null(outcome.Plan.RouteString);
    }

    [Fact]
    public async Task GetPlanAsync_IgnoresSimBriefPilotId()
    {
        var provider = new BuiltInFlightPlanProvider();
        var withPilotId = new FlightPlanRequest(Bristol, Edinburgh, A320, Config, AirlineStrategyProfile.Domestic, SimBriefPilotId: "123456");
        var withoutPilotId = withPilotId with { SimBriefPilotId = null };

        var outcomeWith = await provider.GetPlanAsync(withPilotId, CancellationToken.None);
        var outcomeWithout = await provider.GetPlanAsync(withoutPilotId, CancellationToken.None);

        Assert.Equal(outcomeWithout.Plan, outcomeWith.Plan);
    }
}
