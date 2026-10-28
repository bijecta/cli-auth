namespace Bijecta.CliAuth.Core;

// Explicit capability failure until the real browser/device adapters are implemented.
internal sealed class UnavailableAuthenticationProtocol : IAuthenticationProtocol
{
    public Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options,
        ISystemBrowser browser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromException<ValidatedLogin>(new CliAuthException(AuthErrorCode.UnsupportedCapability));
    }

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RevocationOutcome.Unsupported);
    }
}

internal sealed class UnavailableSystemBrowser : ISystemBrowser
{
    public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new CliAuthException(AuthErrorCode.BrowserUnavailable));
    }
}
