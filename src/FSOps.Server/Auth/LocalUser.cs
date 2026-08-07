namespace FSOps.Server.Auth;

/// <summary>
/// No-op identity used while the app runs entirely on one machine for one person.
/// Fixed GUID so data scoped by UserId stays stable across restarts.
/// </summary>
public sealed class LocalUser : ICurrentUser
{
    private static readonly Guid LocalUserId = new("11111111-1111-1111-1111-111111111111");

    public Guid UserId => LocalUserId;

    public string DisplayName => "Local Pilot";

    public bool IsAuthenticated => true;
}
