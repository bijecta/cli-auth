namespace Bijecta.CliAuth.Core;

// This seam is internal: production implementations must validate the full selected flow.
internal interface IAuthenticationProtocol
{
    Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options,
        ISystemBrowser browser, CancellationToken ct);

    // Returns a validated result or throws a classified CliAuthException: ReauthenticationRequired for invalid_grant,
    // NetworkFailure or DiscoveryFailed for transient failures, ProtocolFailure for other provider errors (ADR 0009).
    // Observes ct until the provider answers; after that it completes validation and returns the result regardless,
    // because the provider may already have rotated the refresh token.
    Task<ValidatedRefresh> RefreshAsync(OidcProfile profile, string refreshToken, string subject,
        IReadOnlyList<string> grantedScopes, CancellationToken ct);

    Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct);
}
