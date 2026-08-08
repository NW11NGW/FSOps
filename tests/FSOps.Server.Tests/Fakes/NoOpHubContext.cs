using FSOps.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// A no-op <see cref="IHubContext{LiveHub}"/> so tests can construct a real
/// <c>FlightLifecycleService</c> without a live SignalR pipeline - every broadcast just completes
/// immediately and goes nowhere, which is fine since nothing in these tests is listening.
/// </summary>
internal sealed class NoOpHubContext : IHubContext<LiveHub>
{
    public IHubClients Clients { get; } = new NoOpHubClients();

    public IGroupManager Groups { get; } = new NoOpGroupManager();

    private sealed class NoOpHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();

        public IClientProxy All => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
