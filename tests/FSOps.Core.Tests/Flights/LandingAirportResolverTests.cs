using FSOps.Core.Entities;
using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class LandingAirportResolverTests
{
    // Real coordinates, matching what RouteTestContext seeds server-side (kept independent here
    // since this class must stay pure and DB-free).
    private static readonly Airport Eggd = MakeAirport("EGGD", 51.3827, -2.7191);
    private static readonly Airport Egph = MakeAirport("EGPH", 55.9500, -3.3725);
    private static readonly Airport Egpf = MakeAirport("EGPF", 55.8719, -4.4331);

    [Fact]
    public void Resolve_FinalPositionAtPlannedArrival_ReportsAnOrdinaryLanding()
    {
        var result = LandingAirportResolver.Resolve(
            [Eggd, Egph, Egpf], (Egph.Latitude, Egph.Longitude), plannedArrivalIcao: "EGPH");

        Assert.Equal("EGPH", result.Icao);
        Assert.Equal(LandingAirportDecision.MatchesPlannedArrival, result.Decision);
    }

    [Fact]
    public void Resolve_FinalPositionAtADifferentAirport_ReportsADiversionAndUsesTheActualAirport()
    {
        // Planned to land at EGPH, but the last tracked position is really at EGPF.
        var result = LandingAirportResolver.Resolve(
            [Eggd, Egph, Egpf], (Egpf.Latitude, Egpf.Longitude), plannedArrivalIcao: "EGPH");

        Assert.Equal("EGPF", result.Icao);
        Assert.Equal(LandingAirportDecision.Diverted, result.Decision);
        Assert.NotNull(result.DistanceFromFinalPositionNm);
        Assert.True(result.DistanceFromFinalPositionNm < LandingAirportResolver.SearchRadiusNm);
    }

    [Fact]
    public void Resolve_NoFinalPosition_FallsBackToPlannedArrivalWithoutGuessing()
    {
        var result = LandingAirportResolver.Resolve([Eggd, Egph, Egpf], finalPosition: null, plannedArrivalIcao: "EGPH");

        Assert.Equal("EGPH", result.Icao);
        Assert.Equal(LandingAirportDecision.NoPositionData, result.Decision);
        Assert.Null(result.DistanceFromFinalPositionNm);
    }

    [Fact]
    public void Resolve_FinalPositionFarFromAnyKnownAirport_FallsBackToPlannedArrival()
    {
        // Mid-Atlantic - nowhere near any of the candidates.
        var result = LandingAirportResolver.Resolve([Eggd, Egph, Egpf], (40.0, -40.0), plannedArrivalIcao: "EGPH");

        Assert.Equal("EGPH", result.Icao);
        Assert.Equal(LandingAirportDecision.UnresolvedFallbackToPlanned, result.Decision);
    }

    [Fact]
    public void Resolve_NoCandidateAirportsAtAll_FallsBackToPlannedArrival()
    {
        var result = LandingAirportResolver.Resolve([], (Egph.Latitude, Egph.Longitude), plannedArrivalIcao: "EGPH");

        Assert.Equal("EGPH", result.Icao);
        Assert.Equal(LandingAirportDecision.UnresolvedFallbackToPlanned, result.Decision);
        Assert.Null(result.DistanceFromFinalPositionNm);
    }

    [Fact]
    public void Resolve_IcaoComparisonIsCaseInsensitive()
    {
        var result = LandingAirportResolver.Resolve([Egph], (Egph.Latitude, Egph.Longitude), plannedArrivalIcao: "egph");

        Assert.Equal(LandingAirportDecision.MatchesPlannedArrival, result.Decision);
    }

    private static Airport MakeAirport(string icao, double latitude, double longitude) => new()
    {
        Icao = icao,
        Name = icao,
        Municipality = icao,
        Country = "GB",
        Latitude = latitude,
        Longitude = longitude,
    };
}
