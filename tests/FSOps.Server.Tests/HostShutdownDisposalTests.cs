using System.Text.Json;
using FSOps.Server.Hubs;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using FSOps.Sim.Fake;
using FSOps.Sim.SimConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace FSOps.Server.Tests;

/// <summary>
/// Guards the shutdown path against the crash that killed the process on every clean exit: the
/// container disposes <see cref="SimTelemetryService"/> - and through it the
/// <see cref="ISimSource"/> - twice, and the second pass reached
/// <c>CancellationTokenSource.Cancel()</c> on a token source the first pass had already disposed.
/// <see cref="ObjectDisposedException"/> from there is thrown out of <c>Host.DisposeAsync()</c>,
/// which is past every catch block in the process, so the process terminated with an unhandled
/// exception. Nine of those were recorded in the Windows Application event log across three days
/// (8, 9 and 11 August 2026), all with byte-identical stacks.
///
/// <para>Two things make it worth a dedicated test file rather than a line in an existing one.
/// First, the failure is invisible to every other kind of test: it needs a real container
/// disposal, and a test that merely constructs the services and lets them fall out of scope will
/// never see it. Second, and the reason this matters beyond one exception - the throw aborts the
/// container's disposal loop part-way, so everything after it in the list is silently skipped.
/// Serilog's logger is in that list. The crash therefore left NOTHING in the app's own log, which
/// is exactly the signature of the unexplained founding crash this investigation was chasing.</para>
///
/// <para>These tests assert the contract, not the one field that happened to throw: disposing
/// twice, and stopping after disposal, must both be no-ops. Double disposal is a property of how
/// these types are registered, so they have to tolerate it outright - guarding only <c>_cts</c>
/// would let the next field added to either class reintroduce the same crash.</para>
/// </summary>
public class HostShutdownDisposalTests
{
    /// <summary>
    /// The mechanism, end to end, in the registration shape Program.cs actually uses: a singleton
    /// that is also registered as a hosted service resolving that same singleton. The container
    /// captures an instance for disposal once per service descriptor, so the one object lands in
    /// the disposal list twice. This test would have failed before the fix, because disposing the
    /// provider is exactly what threw.
    /// </summary>
    [Fact]
    public async Task DisposingTheProvider_DisposesADoublyRegisteredSingletonTwice_AndDoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CountingDisposable>();
        services.AddHostedService(sp => sp.GetRequiredService<CountingDisposable>());

        var provider = services.BuildServiceProvider();
        var instance = provider.GetRequiredService<CountingDisposable>();
        _ = provider.GetServices<IHostedService>().ToList();

        await provider.DisposeAsync();

        // The premise of the whole bug. If this ever drops to 1 the container's behaviour has
        // changed and the guards below become belt-and-braces rather than load-bearing - but they
        // should stay either way, because the registration shape is the thing that invites it.
        Assert.Equal(2, instance.DisposeCount);
    }

    [Fact]
    public async Task FakeSimSource_ToleratesBeingDisposedTwice()
    {
        await using var replay = new TemporaryReplayFile();
        var source = new FakeSimSource(new FakeSimSourceOptions { ReplayFilePath = replay.Path });
        await source.StartAsync(CancellationToken.None);

        await source.DisposeAsync();

        // Before the fix this second pass threw ObjectDisposedException from _cts.Cancel().
        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task FakeSimSource_ToleratesStopAfterDispose()
    {
        await using var replay = new TemporaryReplayFile();
        var source = new FakeSimSource(new FakeSimSourceOptions { ReplayFilePath = replay.Path });
        await source.StartAsync(CancellationToken.None);
        await source.DisposeAsync();

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SimConnectSource_ToleratesBeingDisposedTwice()
    {
        var source = new SimConnectSource(
            new SimConnectSourceOptions { ReconnectInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<SimConnectSource>.Instance);

        // StartAsync is what creates the linked token source the second disposal used to cancel.
        // The connection loop it kicks off never reaches a simulator on a test machine; it fails,
        // logs nothing of interest, and retries, which is exactly what it is documented to do.
        await source.StartAsync(CancellationToken.None);

        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task SimConnectSource_ToleratesStopAfterDispose()
    {
        var source = new SimConnectSource(
            new SimConnectSourceOptions { ReconnectInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<SimConnectSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await source.DisposeAsync();

        await source.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The real shutdown sequence, in order: the host stops the service, then the container
    /// disposes it once per registration. The inner source must survive the whole thing.
    /// </summary>
    [Fact]
    public async Task SimTelemetryService_SurvivesTheRealStopThenDoubleDisposeSequence()
    {
        await using var replay = new TemporaryReplayFile();
        var source = new FakeSimSource(new FakeSimSourceOptions { ReplayFilePath = replay.Path });
        var telemetry = new SimTelemetryService(source, new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);

        await telemetry.StartAsync(CancellationToken.None);
        await telemetry.StopAsync(CancellationToken.None);

        await telemetry.DisposeAsync();
        await telemetry.DisposeAsync();

        // And a stray stop after all of that, which is the other order the host can produce.
        await telemetry.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Counts its own disposals so the container's behaviour can be asserted directly. Implements
    /// <see cref="IHostedService"/> as well because that is the registration shape under test.
    /// </summary>
    private sealed class CountingDisposable : IHostedService, IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The smallest replay <see cref="FakeSimSource.StartAsync"/> will accept, written to a temp
    /// file so these tests never depend on the replay shipped beside the server binary.
    /// </summary>
    private sealed class TemporaryReplayFile : IAsyncDisposable
    {
        public TemporaryReplayFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fsops-replay-{Guid.NewGuid():N}.json");

            var script = new FakeFlightScript
            {
                Aircraft = new FakeAircraft { Title = "Test Aircraft", AtcModel = "A320", AtcType = "A320" },
                Keyframes =
                {
                    new FakeKeyframe { TSeconds = 0, Phase = "Parked", OnGround = true },
                    new FakeKeyframe { TSeconds = 60, Phase = "Parked", OnGround = true },
                },
            };

            File.WriteAllText(Path, JsonSerializer.Serialize(script, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }

            return ValueTask.CompletedTask;
        }
    }
}
