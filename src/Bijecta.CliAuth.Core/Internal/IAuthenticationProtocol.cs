namespace Bijecta.CliAuth.Core;

// This seam is internal: production implementations must validate the full selected flow.
internal interface IAuthenticationProtocol
{
    Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options,
        ISystemBrowser browser, CancellationToken ct);

    Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct);
}
