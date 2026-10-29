namespace Bijecta.CliAuth.Core;

// Keep flow selection internal; both paths return the same validated session candidate.
internal sealed class AuthenticationProtocol : IAuthenticationProtocol
{
    private readonly BrowserAuthenticationProtocol _browser;
    private readonly DeviceAuthenticationProtocol _device;

    internal AuthenticationProtocol(TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _browser = new(time, transport);
        _device = new(time, transport);
    }

    public Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options,
        ISystemBrowser browser, CancellationToken ct) => options.Flow switch
        {
            LoginFlow.Browser => _browser.LoginAsync(profile, options, browser, ct),
            LoginFlow.Device => _device.LoginAsync(profile, options, ct),
            _ => throw new CliAuthException(AuthErrorCode.InvalidConfiguration)
        };

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct) =>
        _browser.RevokeRefreshTokenAsync(profile, refreshToken, ct);
}
