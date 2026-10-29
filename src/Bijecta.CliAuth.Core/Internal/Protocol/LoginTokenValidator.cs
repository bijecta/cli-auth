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
        var value = ProtocolJson.Required(root, "access_token");
        var idToken = ProtocolJson.Required(root, "id_token");
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

        var granted = profile.Scopes;
        if (root.TryGetProperty("scope", out _))
        {
            var scope = ProtocolJson.Required(root, "scope");
            granted = ContractValidation.Scopes(scope.Split(' '), false, AuthErrorCode.ProtocolFailure);
            if (granted.Any(s => !profile.Scopes.Contains(s, StringComparer.Ordinal)))
            {
                throw new CliAuthException(AuthErrorCode.ProtocolFailure);
            }
        }

        string? refresh = null;
        if (root.TryGetProperty("refresh_token", out _))
        {
            refresh = ProtocolJson.Required(root, "refresh_token");
        }

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
        var protocol = new OpenIdConnectProtocolValidator
        {
            RequireNonce = nonce is not null,
            RequireTimeStampInNonce = false,
            RequireSub = true,
            RequireAzp = jwt.Audiences.Count() > 1
        };
        protocol.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
        {
            ClientId = profile.ClientId,
            Nonce = nonce,
            ValidatedIdToken = jwt,
            ProtocolMessage = new OpenIdConnectMessage { IdToken = idToken, AccessToken = value }
        });
        if (jwt.Payload.TryGetValue("azp", out var azp) && (azp is not string authorized || authorized != profile.ClientId))
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        ct.ThrowIfCancellationRequested();
        if (expiresAt - _time.GetUtcNow() <= profile.ExpiryMargin)
        {
            throw new CliAuthException(AuthErrorCode.TokenLifetimeTooShort);
        }
        return new ValidatedLogin(new AccessToken(value, expiresAt, granted), subject, profile.Issuer, profile.ClientId, refresh);
    }

}
