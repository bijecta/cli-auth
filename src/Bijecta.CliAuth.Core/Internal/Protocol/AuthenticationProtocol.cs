namespace Bijecta.CliAuth.Core;

// Keep flow selection internal; both paths return the same validated session candidate.
internal sealed class AuthenticationProtocol : IAuthenticationProtocol
{
    private readonly BrowserAuthenticationProtocol _browser;
    private readonly DeviceAuthenticationProtocol _device;
    private readonly RefreshTokenProtocol _refresh;

    internal AuthenticationProtocol(TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _browser = new(time, transport);
        _device = new(time, transport);
        _refresh = new(time, transport);
    }

    public Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options,
        ISystemBrowser browser, CancellationToken ct) => options.Flow switch
        {
            LoginFlow.Browser => _browser.LoginAsync(profile, options, browser, ct),
            LoginFlow.Device => _device.LoginAsync(profile, options, ct),
            _ => throw new CliAuthException(AuthErrorCode.InvalidConfiguration)
        };

    public Task<ValidatedRefresh> RefreshAsync(OidcProfile profile, string refreshToken, string subject,
        IReadOnlyList<string> grantedScopes, CancellationToken ct) =>
        _refresh.RefreshAsync(profile, refreshToken, subject, grantedScopes, ct);

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct) =>
        _browser.RevokeRefreshTokenAsync(profile, refreshToken, ct);
}
