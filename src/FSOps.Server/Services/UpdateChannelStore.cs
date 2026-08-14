using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Reads and writes the player's chosen update channel.
/// <para>
/// It is an interface for the same reason <see cref="IUpdateStorage"/> is: <see cref="UpdateChecker"/>
/// must be testable without a database behind it, and every rule about which release a channel
/// accepts has to be assertable directly rather than only through a migration and a settings row.
/// </para>
/// </summary>
public interface IUpdateChannelStore
{
    Task<UpdateChannel> GetAsync(CancellationToken ct);

    Task SetAsync(UpdateChannel channel, CancellationToken ct);
}

/// <summary>
/// The live store, backed by <c>UserSettings.UpdateChannel</c>.
///
/// <para><b>Why this reads the database at all, given the updater deliberately does not.</b>
/// <see cref="UpdateState"/> is a JSON file precisely so the updater keeps working when the database
/// does not, and the on/off switch still lives there. The channel is different in kind: it is a user
/// setting, and a setting that lives anywhere other than with the settings is a setting nobody finds.
/// So it goes in the settings table, and the cost of that decision is paid here.</para>
///
/// <para><b>The fallback is load-bearing, not incidental.</b> A database that cannot be opened, a
/// settings row that does not exist yet, and a column holding a value this build does not recognise
/// all resolve to <see cref="UpdateChannel.Stable"/>. That direction is chosen deliberately. Being
/// wrong towards Stable means the player is offered nothing they would otherwise have been offered -
/// an annoyance, visible, recoverable. Being wrong towards Development would mean handing somebody an
/// untested build on the strength of a failed read, which is the one outcome nobody consented to.
/// Anyone changing this: do not "improve" the fallback into remembering the last known value, and do
/// not let a parse failure mean anything other than Stable.</para>
///
/// <para>A scope is created per call rather than holding a DbContext, because
/// <see cref="UpdateChecker"/> is a singleton and <see cref="FsOpsDbContext"/> is not. The read is a
/// single-row local query on a machine-local file; it is nowhere near the startup path and never
/// blocks on a network.</para>
/// </summary>
public sealed class DatabaseUpdateChannelStore : IUpdateChannelStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseUpdateChannelStore> _logger;

    public DatabaseUpdateChannelStore(IServiceScopeFactory scopeFactory, ILogger<DatabaseUpdateChannelStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<UpdateChannel> GetAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
            var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

            var channel = await db.UserSettings
                .Where(s => s.OwnerUserId == currentUser.UserId)
                .Select(s => (UpdateChannel?)s.UpdateChannel)
                .FirstOrDefaultAsync(ct);

            // No row yet is the ordinary state of a brand-new install, not a fault. It means Stable,
            // and it must not be turned into a write - creating a settings row as a side effect of
            // asking a question is how a "default" quietly becomes a stored choice.
            return channel ?? UpdateChannel.Stable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The update channel could not be read - falling back to the stable channel");
            return UpdateChannel.Stable;
        }
    }

    public async Task SetAsync(UpdateChannel channel, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == currentUser.UserId, ct);
        if (settings is null)
        {
            settings = new UserSettings { Id = Guid.NewGuid(), OwnerUserId = currentUser.UserId };
            db.UserSettings.Add(settings);
        }

        settings.UpdateChannel = channel;
        await db.SaveChangesAsync(ct);
    }
}
