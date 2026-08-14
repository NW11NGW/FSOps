using FSOps.Core.Entities;
using FSOps.Server.Services;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// An in-memory update channel, so the checker's channel rules can be asserted without a database.
/// <para>
/// It defaults to <see cref="UpdateChannel.Stable"/> for the same reason the real store does, and
/// that default is worth stating rather than assuming: a fake that started on Development would let
/// a regression in the real default pass every test in the suite.
/// </para>
/// </summary>
internal sealed class FakeUpdateChannelStore : IUpdateChannelStore
{
    public FakeUpdateChannelStore(UpdateChannel channel = UpdateChannel.Stable) => Channel = channel;

    public UpdateChannel Channel { get; private set; }

    /// <summary>How many times the channel has been written. Used to prove that merely reading the
    /// status never stores a choice the user did not make.</summary>
    public int WriteCount { get; private set; }

    public Task<UpdateChannel> GetAsync(CancellationToken ct) => Task.FromResult(Channel);

    public Task SetAsync(UpdateChannel channel, CancellationToken ct)
    {
        Channel = channel;
        WriteCount++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A channel store that cannot answer - what an unopenable or missing database looks like from the
/// checker's side. The real <c>DatabaseUpdateChannelStore</c> swallows this and returns Stable; this
/// fake throws instead, so a test can prove the checker survives a store that does not.
/// </summary>
internal sealed class ThrowingUpdateChannelStore : IUpdateChannelStore
{
    public Task<UpdateChannel> GetAsync(CancellationToken ct) =>
        Task.FromException<UpdateChannel>(new InvalidOperationException("the database is not available"));

    public Task SetAsync(UpdateChannel channel, CancellationToken ct) =>
        Task.FromException(new InvalidOperationException("the database is not available"));
}
