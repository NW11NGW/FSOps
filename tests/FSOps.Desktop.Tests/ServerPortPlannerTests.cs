using FSOps.Desktop;

namespace FSOps.Desktop.Tests;

/// <summary>
/// The rules that decide where the desktop shell's UI points. These matter because two of the
/// three outcomes are invisible when they go wrong: attaching to a server the shell did not start
/// means it must not kill it on close, and moving to a fallback port means the in-game panel's
/// stored port can go stale. Both are decided here.
/// </summary>
public class ServerPortPlannerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("5977 ")]
    public void An_unusable_FSOPS_PORT_falls_back_to_the_documented_default(string? configured)
    {
        // A typo in an environment variable must never be the reason the app will not open.
        Assert.Equal(ServerPortPlanner.DefaultPort, ServerPortPlanner.ResolvePreferredPort(configured));
    }

    [Fact]
    public void A_valid_FSOPS_PORT_is_honoured()
    {
        Assert.Equal(5992, ServerPortPlanner.ResolvePreferredPort("5992"));
    }

    [Fact]
    public void A_free_preferred_port_means_start_our_own_server_there()
    {
        var decision = ServerPortPlanner.Decide(5977, _ => PortState.Free);

        Assert.NotNull(decision);
        Assert.Equal(5977, decision!.Value.Port);
        Assert.Equal(ServerStartMode.StartOwnServer, decision.Value.Mode);
    }

    [Fact]
    public void An_FSOps_server_already_on_the_preferred_port_is_attached_to_not_replaced()
    {
        // There is one SQLite database per user. Starting a second server against a file the first
        // already has open is how a double-click turns into a corrupted ledger.
        var decision = ServerPortPlanner.Decide(5977, _ => PortState.FsOpsServer);

        Assert.NotNull(decision);
        Assert.Equal(5977, decision!.Value.Port);
        Assert.Equal(ServerStartMode.AttachToRunningServer, decision.Value.Mode);
    }

    [Fact]
    public void A_preferred_port_taken_by_other_software_steps_to_the_next_free_one()
    {
        var probed = new List<int>();
        var decision = ServerPortPlanner.Decide(5977, port =>
        {
            probed.Add(port);
            return port < 5979 ? PortState.SomethingElse : PortState.Free;
        });

        Assert.NotNull(decision);
        Assert.Equal(5979, decision!.Value.Port);
        Assert.Equal(ServerStartMode.StartOwnServer, decision.Value.Mode);
        Assert.Equal([5977, 5978, 5979], probed);
    }

    [Fact]
    public void An_FSOps_server_found_further_up_the_range_is_still_attached_to()
    {
        var decision = ServerPortPlanner.Decide(5977, port => port switch
        {
            5977 => PortState.SomethingElse,
            5978 => PortState.FsOpsServer,
            _ => PortState.Free,
        });

        Assert.NotNull(decision);
        Assert.Equal(5978, decision!.Value.Port);
        Assert.Equal(ServerStartMode.AttachToRunningServer, decision.Value.Mode);
    }

    [Fact]
    public void A_completely_occupied_range_returns_no_decision_rather_than_guessing()
    {
        // The caller turns this into a real error message. Silently binding somewhere unexpected
        // would leave the in-game panel pointing at nothing with no explanation.
        Assert.Null(ServerPortPlanner.Decide(5977, _ => PortState.SomethingElse));
    }

    [Fact]
    public void The_search_stays_within_the_documented_fallback_range()
    {
        var probed = new List<int>();
        ServerPortPlanner.Decide(5977, port =>
        {
            probed.Add(port);
            return PortState.SomethingElse;
        });

        Assert.Equal(ServerPortPlanner.FallbackPortsToTry + 1, probed.Count);
        Assert.Equal(5977, probed[0]);
        Assert.Equal(5977 + ServerPortPlanner.FallbackPortsToTry, probed[^1]);
    }

    [Fact]
    public void The_search_never_runs_past_the_top_of_the_port_range()
    {
        var probed = new List<int>();
        var decision = ServerPortPlanner.Decide(65534, port =>
        {
            probed.Add(port);
            return PortState.SomethingElse;
        });

        Assert.Null(decision);
        Assert.Equal([65534, 65535], probed);
    }
}
