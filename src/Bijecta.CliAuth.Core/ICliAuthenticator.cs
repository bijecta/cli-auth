namespace Bijecta.CliAuth.Core;

/// <summary>Framework-independent authentication operations; only LoginAsync initiates interaction.</summary>
public interface ICliAuthenticator
{
    /// <summary>Explicitly logs into a named profile using the selected browser or device flow.</summary>
    Task<LoginResult> LoginAsync(string profile, LoginOptions options, CancellationToken cancellationToken = default);
    /// <summary>Acquires a usable access token silently, refreshing when supported.</summary>
    Task<AccessToken> GetAccessTokenAsync(string profile, CancellationToken cancellationToken = default);
    /// <summary>Reports local status without discovery or refresh.</summary>
    Task<SessionStatus> GetStatusAsync(string profile, CancellationToken cancellationToken = default);
    /// <summary>Reports local status for every configured profile, in configuration order, without discovery or refresh.</summary>
    /// <remarks>A profile whose record cannot be read is reported in its own entry rather than failing the list.</remarks>
    Task<IReadOnlyList<ProfileSessionStatus>> ListSessionsAsync(CancellationToken cancellationToken = default);
    /// <summary>Removes local credentials before any optional best-effort revocation.</summary>
    Task<LogoutResult> LogoutAsync(string profile, LogoutOptions options, CancellationToken cancellationToken = default);
}
