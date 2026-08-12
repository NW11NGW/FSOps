using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Money;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Regression coverage for the starting-loan principal cap added to AirlineEndpoints.CreateAsync
/// after a silent-crash investigation. The investigation ruled out the loan calculators themselves
/// (LoanCalculator/LoanEligibilityCalculator/LoanRateCalculator all proven safe at the reported
/// figures - see tests/FSOps.Core.Tests/Finance/StartupLoanCrashReproTests.cs, which stays as a
/// record of what was ruled out) and instead found that a starting loan had NO principal cap at
/// all - unlike FleetEndpoints.TakeLoanAsync's mid-game loan, which LoanEligibilityCalculator
/// already bounds against trailing cash flow. A starting loan can never reuse that check (a
/// brand-new airline's trailing cash flow is always exactly 0, so MaxMonthlyPayment(0) == 0 would
/// reject every starting loan of any size, not bound it) - see
/// <see cref="FSOps.Core.Economy.LoanConfig.MaxStartingLoanPrincipal"/>'s own doc for the fix that
/// was chosen instead: a flat, hand-authored per-playstyle ceiling, refused outright with both the
/// requested and maximum figures named - never a crash, never a silent clamp to a smaller loan than
/// requested.
///
/// Drives <see cref="AirlineEndpoints.CreateAsync"/> directly against a fresh, airline-free
/// database - unlike <see cref="RouteTestContext"/>, which always seeds one airline per user and so
/// can't be reused here (CreateAsync refuses a second airline for the same owner).
/// </summary>
public sealed class AirlineCreationLoanCapTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FsOpsDbContext _db;
    private readonly ICurrentUser _currentUser = new LocalUser();
    private readonly EconomyConfigCatalog _catalog = EconomyConfigCatalog.Default();

    public AirlineCreationLoanCapTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(_connection).Options;
        _db = new FsOpsDbContext(options);
        _db.Database.Migrate();

        _db.Airports.Add(new Airport
        {
            Icao = "EGGD",
            Iata = "BRS",
            Name = "Bristol Airport",
            Municipality = "Bristol",
            Country = "GB",
            Latitude = 51.3827,
            Longitude = -2.7191,
            ElevationFt = 622,
            SizeCategory = AirportSizeCategory.Medium,
            HasScheduledService = true,
            LongestRunwayFt = 8000,
        });

        _db.AircraftTypes.Add(new AircraftType
        {
            Id = Guid.NewGuid(),
            IcaoType = "A320",
            Family = "A320",
            Manufacturer = "Airbus",
            Name = "A320neo",
            PaxCapacity = 180,
            RangeNm = 3400,
            CruiseTasKts = 450,
            FuelBurnKgPerHour = 2400,
            MtowTonnes = 78.0,
            MinRunwayFt = 5500,
            ServiceCeilingFt = 39000,
            PurchasePrice = 100_000_000m,
            MonthlyLeaseRate = 500_000m,
            MatchPatterns = "[]",
        });

        _db.SaveChanges();
    }

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static object BodyOf(IResult result) => Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    // Every RequestWithLoan below passes CurrencyCode: "GBP" - the refusal message formats both
    // figures through MoneyFormatter for display (see AirlineEndpoints.CreateAsync's own comment on
    // why it reads `currency` from the REQUEST rather than saved settings: no airline/UserSettings
    // row exists yet at this point). GBP is the base currency (DisplayRate 1.00), so the formatted
    // figure differs from the raw decimal only in the £ symbol and thousands separator - asserting
    // against MoneyFormatter's own output, not a hand-built string, means this test can't silently
    // drift from whatever MoneyFormatter actually produces.
    private static readonly Currency Gbp = CurrencyCatalogue.TryGet("GBP")!;

    private CreateAirlineRequest RequestWithLoan(string playstyle, string icaoCode, decimal amount, int termMonths = 60) =>
        new(
            Name: "Loan Cap Test Airline",
            IcaoCode: icaoCode,
            HomeAirportIcao: "EGGD",
            StrategyProfile: "Domestic",
            Playstyle: playstyle,
            AccentColour: "#3b82f6",
            StarterAircraftFamily: "A320",
            CurrencyCode: "GBP",
            StartingLoan: new StartingLoanRequest(amount, termMonths));

    [Fact]
    public async Task CasualStartingLoan_AboveTheCap_IsRefused_NamingBothTheRequestedAndMaxFigures()
    {
        var casualCap = _catalog.Get(AirlinePlaystyle.Casual).Loan.MaxStartingLoanPrincipal;
        var request = RequestWithLoan("Casual", "LCA", casualCap + 1m);

        var result = await AirlineEndpoints.CreateAsync(request, _db, _currentUser, _catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.False(await _db.Airlines.AnyAsync(a => a.IcaoCode == "LCA"), "A refused starting loan must not leave a half-created airline behind.");

        var body = BodyOf(result);
        var requestedAmount = (decimal)body.GetType().GetProperty("requestedAmount")!.GetValue(body)!;
        var maxStartingLoanPrincipal = (decimal)body.GetType().GetProperty("maxStartingLoanPrincipal")!.GetValue(body)!;
        var error = (string)body.GetType().GetProperty("error")!.GetValue(body)!;

        Assert.Equal(casualCap + 1m, requestedAmount);
        Assert.Equal(casualCap, maxStartingLoanPrincipal);
        // Asserted against MoneyFormatter's own formatted output (e.g. "£250,000.00"), not a raw
        // decimal ToString - a fragment like "250" would still pass on a regression back to
        // unformatted output and prove nothing.
        Assert.Contains(MoneyFormatter.Format(casualCap, Gbp), error);
        Assert.Contains(MoneyFormatter.Format(casualCap + 1m, Gbp), error);
        Assert.Contains("Casual airline", error);
    }

    [Fact]
    public async Task CasualStartingLoan_AtExactlyTheCap_Succeeds()
    {
        var casualCap = _catalog.Get(AirlinePlaystyle.Casual).Loan.MaxStartingLoanPrincipal;
        var request = RequestWithLoan("Casual", "LCB", casualCap);

        var result = await AirlineEndpoints.CreateAsync(request, _db, _currentUser, _catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var loan = await _db.Loans.SingleAsync(l => l.Principal == casualCap);
        Assert.Equal(casualCap, loan.Principal);
    }

    [Fact]
    public async Task TrueLifeStartingLoan_AtCasualsCap_Succeeds_ButAboveItsOwnLargerCap_IsRefused()
    {
        // Proves the cap is genuinely per playstyle, not one shared figure - Casual's cap
        // (250,000) is comfortably inside True-life's own (5,000,000), so a loan at Casual's cap
        // must succeed under True-life, while a loan above True-life's own (much larger) cap must
        // still be refused.
        var casualCap = _catalog.Get(AirlinePlaystyle.Casual).Loan.MaxStartingLoanPrincipal;
        var trueLifeCap = _catalog.Get(AirlinePlaystyle.TrueLife).Loan.MaxStartingLoanPrincipal;
        Assert.True(trueLifeCap > casualCap, "This test assumes True-life's cap is the larger of the two.");

        var withinBothCapsResult = await AirlineEndpoints.CreateAsync(
            RequestWithLoan("TrueLife", "LCC", casualCap), _db, _currentUser, _catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(withinBothCapsResult));

        using var secondConnection = new SqliteConnection("Filename=:memory:");
        secondConnection.Open();
        var secondOptions = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(secondConnection).Options;
        using var secondDb = new FsOpsDbContext(secondOptions);
        await secondDb.Database.MigrateAsync();
        secondDb.Airports.Add(new Airport
        {
            Icao = "EGGD", Iata = "BRS", Name = "Bristol Airport", Municipality = "Bristol", Country = "GB",
            Latitude = 51.3827, Longitude = -2.7191, ElevationFt = 622, SizeCategory = AirportSizeCategory.Medium,
            HasScheduledService = true, LongestRunwayFt = 8000,
        });
        secondDb.AircraftTypes.Add(new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Manufacturer = "Airbus", Name = "A320neo",
            PaxCapacity = 180, RangeNm = 3400, CruiseTasKts = 450, FuelBurnKgPerHour = 2400, MtowTonnes = 78.0,
            MinRunwayFt = 5500, ServiceCeilingFt = 39000, PurchasePrice = 100_000_000m, MonthlyLeaseRate = 500_000m,
            MatchPatterns = "[]",
        });
        await secondDb.SaveChangesAsync();
        var secondUser = new LocalUser();

        var aboveTrueLifeCapResult = await AirlineEndpoints.CreateAsync(
            RequestWithLoan("TrueLife", "LCD", trueLifeCap + 1m), secondDb, secondUser, _catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(aboveTrueLifeCapResult));

        // The refusal must read "True-life", never the raw enum name "TrueLife" - and the maximum
        // it names must be True-life's own (much larger) cap, not Casual's.
        var error = (string)BodyOf(aboveTrueLifeCapResult).GetType().GetProperty("error")!.GetValue(BodyOf(aboveTrueLifeCapResult))!;
        Assert.Contains("True-life airline", error);
        Assert.DoesNotContain("TrueLife airline", error);
        Assert.Contains(MoneyFormatter.Format(trueLifeCap, Gbp), error);
        Assert.Contains(MoneyFormatter.Format(trueLifeCap + 1m, Gbp), error);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
