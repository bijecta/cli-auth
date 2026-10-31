using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.Core;

internal sealed class LoginTokenValidator(TimeProvider _time, ProviderDiscovery _discovery)
{
    internal async Task<ValidatedLogin> ValidateResponseAsync(OidcProfile profile, ProviderMetadata provider,
        string raw, string? nonce, DateTimeOffset started, CancellationToken ct)
    {
        using var json = ProtocolJson.Parse(raw);
        var root = json.RootElement;
        var token = ParseAccessToken(root, profile.Scopes, profile.Scopes, started);
        var idToken = ProtocolJson.Required(root, "id_token");
        var refresh = OptionalRefreshToken(root);
        var subject = await ValidateIdTokenAsync(profile, provider, idToken, token.Value, nonce, refreshing: false, ct)
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (token.ExpiresAtUtc - _time.GetUtcNow() <= profile.ExpiryMargin)
        {
            throw new CliAuthException(AuthErrorCode.TokenLifetimeTooShort);
        }
        return new ValidatedLogin(token, subject, profile.Issuer, profile.ClientId, refresh);
    }

    // Refresh responses follow the login rules, except that the ID token is optional and, when present, must name the
    // session's subject (OIDC Core 12.2). An absent scope keeps the granted scopes (RFC 6749 5.1). The lifetime check
    // against the expiry margin is the coordinator's, after the commit (ADR 0009).
    internal async Task<ValidatedRefresh> ValidateRefreshAsync(OidcProfile profile, ProviderMetadata provider,
        string raw, string subject, IReadOnlyList<string> grantedScopes, DateTimeOffset started, CancellationToken ct)
    {
        using var json = ProtocolJson.Parse(raw);
        var root = json.RootElement;
        var token = ParseAccessToken(root, grantedScopes, grantedScopes, started);
        var refresh = OptionalRefreshToken(root);
        if (root.TryGetProperty("id_token", out _))
        {
            var idToken = ProtocolJson.Required(root, "id_token");
            var refreshed = await ValidateIdTokenAsync(profile, provider, idToken, token.Value, nonce: null, refreshing: true, ct)
                .ConfigureAwait(false);
            if (!string.Equals(refreshed, subject, StringComparison.Ordinal))
            {
                throw new CliAuthException(AuthErrorCode.ProtocolFailure);
            }
        }

        ct.ThrowIfCancellationRequested();
        return new ValidatedRefresh(token, refresh);
    }

    private static AccessToken ParseAccessToken(JsonElement root, IReadOnlyList<string> allowedScopes,
        IReadOnlyList<string> defaultScopes, DateTimeOffset started)
    {
        var value = ProtocolJson.Required(root, "access_token");
        var type = ProtocolJson.Required(root, "token_type");
        if (!string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number ||
            !expiry.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0 ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = started.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        var granted = defaultScopes;
        if (root.TryGetProperty("scope", out _))
        {
            var scope = ProtocolJson.Required(root, "scope");
            granted = ContractValidation.Scopes(scope.Split(' '), false, AuthErrorCode.ProtocolFailure);
            if (granted.Any(s => !allowedScopes.Contains(s, StringComparer.Ordinal)))
            {
                throw new CliAuthException(AuthErrorCode.ProtocolFailure);
            }
        }

        return new AccessToken(value, expiresAt, granted, obtainedAtUtc: started);
    }

    private static string? OptionalRefreshToken(JsonElement root) =>
        root.TryGetProperty("refresh_token", out _) ? ProtocolJson.Required(root, "refresh_token") : null;

    private async Task<string> ValidateIdTokenAsync(OidcProfile profile, ProviderMetadata provider, string idToken,
        string accessToken, string? nonce, bool refreshing, CancellationToken ct)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
            MaximumTokenSizeInBytes = 128 * 1024
        };
        var untrusted = handler.ReadJwtToken(idToken);
        if (untrusted.Header.Alg is not (SecurityAlgorithms.RsaSha256 or SecurityAlgorithms.EcdsaSha256))
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        // Exactly one refresh for an unknown kid. No retry of the token POST or a bad known-key signature.
        if (!string.IsNullOrEmpty(untrusted.Header.Kid) && !provider.Keys.Any(k => k.KeyId == untrusted.Header.Kid))
        {
            provider = await _discovery.ReloadKeysAsync(provider, ct).ConfigureAwait(false);
        }

        var principal = handler.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = profile.Issuer.OriginalString,
            ValidateAudience = true,
            ValidAudience = profile.ClientId,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = provider.Keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
            LifetimeValidator = (notBefore, expires, _, _) => expires.HasValue &&
                (!notBefore.HasValue || notBefore <= expires && notBefore <= _time.GetUtcNow().UtcDateTime.AddSeconds(60)) &&
                expires > _time.GetUtcNow().UtcDateTime.AddSeconds(-60)
        }, out var validated);
        var jwt = (JwtSecurityToken)validated;
        OpenIdConnectProtocolValidator protocol = refreshing ? new RefreshProtocolValidator() : new OpenIdConnectProtocolValidator
        {
            RequireNonce = nonce is not null
        };
        protocol.RequireTimeStampInNonce = false;
        protocol.RequireSub = true;
        protocol.RequireAzp = jwt.Audiences.Count() > 1;
        protocol.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
        {
            ClientId = profile.ClientId,
            Nonce = nonce,
            ValidatedIdToken = jwt,
            ProtocolMessage = new OpenIdConnectMessage { IdToken = idToken, AccessToken = accessToken }
        });
        if (jwt.Payload.TryGetValue("azp", out var azp) && (azp is not string authorized || authorized != profile.ClientId))
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        var subject = principal.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(subject) ? throw new CliAuthException(AuthErrorCode.ProtocolFailure) : subject;
    }

    // A refreshed ID token may repeat the original login's nonce (OIDC Core 12.2), which is not stored.
    private sealed class RefreshProtocolValidator : OpenIdConnectProtocolValidator
    {
        protected override void ValidateNonce(OpenIdConnectProtocolValidationContext validationContext) { }
    }
}
