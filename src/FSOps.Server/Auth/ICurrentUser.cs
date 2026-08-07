namespace FSOps.Server.Auth;

/// <summary>
/// Identity of the caller making the current request. Every endpoint and hub method
/// should depend on this instead of reading identity off HttpContext directly, and every
/// query that returns owned data should filter by UserId. Locally there is only ever one
/// user (see <see cref="LocalUser"/>), but routing all data access through this interface
/// now means swapping in real OIDC/JWT auth later is a DI registration change, not an
/// endpoint rewrite.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }

    string DisplayName { get; }

    bool IsAuthenticated { get; }
}
