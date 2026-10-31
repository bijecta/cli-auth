namespace Bijecta.CliAuth.Core;

/// <summary>Coordinates named profiles sharing one explicitly selected store and lock domain.</summary>
/// <remarks>Browser login uses authorization code with PKCE. Device login uses host presentation and polling. Token acquisition refreshes silently under the session lock (ADR 0009). Remote revocation is deferred.</remarks>
public sealed class CliAuthenticator : ICliAuthenticator
{
    private readonly SessionCoordinator _coordinator;

    /// <summary>Creates an authenticator using local validation only, without discovery or store I/O.</summary>
    /// <param name="profiles">Immutable named profiles sharing the store's persistence policy.</param>
    /// <param name="tokenStore">The explicitly selected backend, owned by the host.</param>
    /// <param name="sessionLocks">Matching locks; omitted when the store itself implements the lock provider.</param>
    /// <param name="browser">Optional host browser launcher; defaults to the system browser.</param>
    /// <param name="timeProvider">Clock for expiry decisions; defaults to the system clock.</param>
    /// <param name="sessionLockTimeout">Positive bounded wait; defaults to 30 seconds.</param>
    public CliAuthenticator(IEnumerable<OidcProfile> profiles, ITokenStore tokenStore,
        ISessionLockProvider? sessionLocks = null, ISystemBrowser? browser = null,
        TimeProvider? timeProvider = null, TimeSpan? sessionLockTimeout = null)
        : this(profiles, tokenStore, new AuthenticationProtocol(timeProvider), sessionLocks,
            browser, timeProvider, sessionLockTimeout)
    {
    }

    internal CliAuthenticator(IEnumerable<OidcProfile> profiles, ITokenStore tokenStore,
        IAuthenticationProtocol protocol, ISessionLockProvider? sessionLocks = null,
        ISystemBrowser? browser = null, TimeProvider? timeProvider = null, TimeSpan? sessionLockTimeout = null)
    {
        _coordinator = new SessionCoordinator(profiles, tokenStore, protocol, sessionLocks,
            browser, timeProvider, sessionLockTimeout);
    }

    /// <inheritdoc />
    public Task<LoginResult> LoginAsync(string profile, LoginOptions options, CancellationToken cancellationToken = default) =>
        _coordinator.LoginAsync(profile, options, cancellationToken);

    /// <inheritdoc />
    public Task<AccessToken> GetAccessTokenAsync(string profile, CancellationToken cancellationToken = default) =>
        _coordinator.GetAccessTokenAsync(profile, cancellationToken);

    /// <inheritdoc />
    public Task<SessionStatus> GetStatusAsync(string profile, CancellationToken cancellationToken = default) =>
        _coordinator.GetStatusAsync(profile, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProfileSessionStatus>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        _coordinator.ListSessionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<LogoutResult> LogoutAsync(string profile, LogoutOptions options, CancellationToken cancellationToken = default) =>
        _coordinator.LogoutAsync(profile, options, cancellationToken);
}
