using FSOps.Core.Economy;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Both wall-clock catch-up services run their first pass as the opening statement of
/// <c>ExecuteAsync</c>, before it has yielded. That is deliberate - a catch-up after the app has
/// been closed for days should happen at once rather than up to a tick later - but it means
/// <see cref="BackgroundService"/> hands the host an already-faulted task if that pass throws, and
/// the exception propagates out of <c>Host.StartAsync()</c> into <c>Main</c>. It never reaches
/// <c>BackgroundServiceExceptionBehavior</c>, so the host's own "BackgroundService failed" critical
/// log never runs: the process died with nothing whatsoever in the app's log. That was observed for
/// real, from <c>EconomyClockService.RunOnceAsync</c>, when the database was unreadable.
///
/// <para>The agreed behaviour is catch, log at Critical, record it where the UI can see it, and
/// carry on. Killing the app is wrong on its own terms: FSOps is usable with the catch-up not yet
/// done, and the periodic timer retries a minute later, so a transient database problem must not
/// cost the player the whole application. These tests assert exactly that - a throwing first pass
/// does not bring the host down, and the service goes on to its timer loop rather than ending.</para>
///
/// <para>Failure is injected through <see cref="IServiceScopeFactory"/> rather than by corrupting a
/// database, because it is the only dependency both services touch before anything else and it
/// makes the throw deterministic. What is under test is the failure policy, not which particular
/// exception triggered it.</para>
/// </summary>
public class StartupCatchUpFailureTests
{
    [Fact]
    public async Task EconomyClockService_StartupPassThatThrows_DoesNotBringDownTheHost()
    {
        var scopes = new ThrowingScopeFactory();
        var state = new StartupReconciliationState();
        var service = new EconomyClockService(
            scopes, EconomyConfigCatalog.Default(), new FakeClock(Instant), NullLogger<EconomyClockService>.Instance, state);

        // This is the line that used to take the process with it.
        await ((IHostedService)service).StartAsync(CancellationToken.None);

        Assert.True(scopes.Attempts >= 1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EconomyClockService_StartupPassThatThrows_ReachesItsTimerLoopAndIsRecorded()
    {
        var scopes = new ThrowingScopeFactory();
        var state = new StartupReconciliationState();
        var service = new EconomyClockService(
            scopes, EconomyConfigCatalog.Default(), new FakeClock(Instant), NullLogger<EconomyClockService>.Instance, state);

        await ((IHostedService)service).StartAsync(CancellationToken.None);

        // Not merely "did not throw": the service must still be alive and waiting on its timer, so
        // the next tick actually retries. A completed ExecuteTask would mean it had given up.
        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsCompleted);

        // And the failure must be visible to the app, not just to a log file - a pass that failed
        // means lease/salary/insurance charges may be missing, which the player has to be told.
        var failure = Assert.Single(state.CatchUpFailures);
        Assert.Equal(nameof(EconomyClockService), failure.Service);
        Assert.Equal(Instant, failure.OccurredUtc);
        Assert.Contains(ThrowingScopeFactory.Marker, failure.Message);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task VirtualFlightResolverService_StartupPassThatThrows_DoesNotBringDownTheHost()
    {
        var scopes = new ThrowingScopeFactory();
        var state = new StartupReconciliationState();
        var service = new VirtualFlightResolverService(
            scopes, EconomyConfigCatalog.Default(), new FakeClock(Instant), NullLogger<VirtualFlightResolverService>.Instance, state);

        await ((IHostedService)service).StartAsync(CancellationToken.None);

        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsCompleted);

        var failure = Assert.Single(state.CatchUpFailures);
        Assert.Equal(nameof(VirtualFlightResolverService), failure.Service);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Both services record into the same state object from their own threads, so the list has to
    /// hold up under concurrent writes rather than merely happening not to be hit at once.
    /// </summary>
    [Fact]
    public void StartupReconciliationState_RecordsFailuresFromManyThreadsWithoutLoss()
    {
        var state = new StartupReconciliationState();

        Parallel.For(0, 200, i =>
            state.RecordCatchUpFailure($"service{i % 2}", new InvalidOperationException($"boom {i}"), Instant));

        Assert.Equal(200, state.CatchUpFailures.Count);
    }

    [Fact]
    public void StartupReconciliationState_HasNoFailuresWhenNothingWentWrong()
    {
        Assert.Empty(new StartupReconciliationState().CatchUpFailures);
    }

    private static readonly DateTimeOffset Instant = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Fails every attempt to open a scope, which is the first thing either service's
    /// <c>RunOnceAsync</c> does. Counts attempts so a test can prove the pass was really tried.
    /// </summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public const string Marker = "scope factory refused";

        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException(Marker);
        }
    }
}
