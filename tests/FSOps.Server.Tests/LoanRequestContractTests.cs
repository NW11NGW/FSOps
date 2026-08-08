using System.Text.Json;

using FSOps.Server.Endpoints;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E1's own stated requirement, verified against the wire contract itself, not just the
/// endpoint logic - see docs/PLAN.md "Loan interest is set by the simulation, never by the player".
/// <see cref="StartingLoanRequest"/> (AirlineEndpoints.CreateAsync's starting loan) and
/// <see cref="TakeLoanRequest"/> (FleetEndpoints.TakeLoanAsync's mid-game loan) have NO rate
/// property at all, so a request body still carrying "annualRatePct" - an old client, or a
/// deliberate probe for the 0% exploit the plan describes - is silently dropped by JSON
/// deserialization rather than read, validated, or trusted. This is stronger than a runtime check:
/// the exploit is impossible to express in the type, not merely rejected after the fact.
/// </summary>
public class LoanRequestContractTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void StartingLoanRequest_HasNoRateProperty_AndDropsOneSentInTheRequestBody()
    {
        const string json = """{ "amount": 500000, "termMonths": 60, "annualRatePct": 0 }""";

        var request = JsonSerializer.Deserialize<StartingLoanRequest>(json, Options);

        Assert.NotNull(request);
        Assert.Equal(500_000m, request!.Amount);
        Assert.Equal(60, request.TermMonths);
        Assert.DoesNotContain("AnnualRatePct", typeof(StartingLoanRequest).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void TakeLoanRequest_HasNoRateProperty_AndDropsOneSentInTheRequestBody()
    {
        const string json = """{ "amount": 200000, "termMonths": 36, "annualRatePct": 0 }""";

        var request = JsonSerializer.Deserialize<TakeLoanRequest>(json, Options);

        Assert.NotNull(request);
        Assert.Equal(200_000m, request!.Amount);
        Assert.Equal(36, request.TermMonths);
        Assert.DoesNotContain("AnnualRatePct", typeof(TakeLoanRequest).GetProperties().Select(p => p.Name));
    }
}
