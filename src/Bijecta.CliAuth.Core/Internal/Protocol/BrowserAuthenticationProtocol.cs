using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.Core;

internal sealed class BrowserAuthenticationProtocol : IAuthenticationProtocol
{
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ProviderDiscovery _discovery;

    internal BrowserAuthenticationProtocol(TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _time = time ?? TimeProvider.System;
        _http = ProtocolHttp.Create(_time, transport);
        _discovery = new ProviderDiscovery(_http, _time);
    }

    public async Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options, ISystemBrowser browser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.Flow != LoginFlow.Browser)
        {
            throw new CliAuthException(AuthErrorCode.UnsupportedCapability);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            return await BrowserLoginAsync(profile, options, browser, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CliAuthException(AuthErrorCode.LoginTimedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CliAuthException)
        {
            throw;
        }
        catch
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }
    }

    private async Task<ValidatedLogin> BrowserLoginAsync(OidcProfile profile, LoginOptions options, ISystemBrowser browser, CancellationToken ct)
    {
        var provider = await _discovery.GetAsync(profile, ct).ConfigureAwait(false);
        await using var receiver = new LoopbackReceiver(profile.Callback, profile.Issuer, _time);
        var client = new OidcClient(new OidcClientOptions
        {
            Authority = profile.Issuer.OriginalString,
            ClientId = profile.ClientId,
            Scope = string.Join(' ', profile.Scopes),
            RedirectUri = receiver.RedirectUri.AbsoluteUri,
            LoadProfile = false,
            FilterClaims = false,
            StateLength = 32,
            DisablePushedAuthorization = true,
            LoggerFactory = NullLoggerFactory.Instance,
            ProviderInformation = new ProviderInformation
            {
                IssuerName = profile.Issuer.OriginalString,
                AuthorizeEndpoint = provider.AuthorizationEndpoint.AbsoluteUri,
                TokenEndpoint = provider.TokenEndpoint.AbsoluteUri,
                KeySet = new Duende.IdentityModel.Jwk.JsonWebKeySet(provider.Jwks)
            }
        });
        // Duende prepares S256/state. Nonce and complete ID-token validation are explicit requirements here.
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var front = new Parameters { { "nonce", nonce } };
        if (options.LoginHint is not null)
        {
            front.Add("login_hint", options.LoginHint);
        }

        if (options.ForceReauthentication)
        {
            front.Add("prompt", "login");
        }

        var state = await client.PrepareLoginAsync(front, ct).ConfigureAwait(false);
        receiver.Start(state.State);
        try
        {
            await browser.OpenAsync(new Uri(state.StartUrl), ct).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CliAuthException)
        {
            throw;
        }
        catch
        {
            throw new CliAuthException(AuthErrorCode.BrowserUnavailable);
        }

        var callback = await receiver.Response.WaitAsync(ct).ConfigureAwait(false);
        if (callback.Error is not null)
        {
            throw new CliAuthException(callback.Error == "access_denied" ? AuthErrorCode.AccessDenied : AuthErrorCode.ProtocolFailure, callback.Error);
        }

        var started = _time.GetUtcNow();
        // Use the endpoint helper rather than ProcessResponseAsync: expiry, scopes, mandatory ID token,
        // and validation clock are governed by this library's stricter policy.
        var response = await _http.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = provider.TokenEndpoint.AbsoluteUri,
            ClientId = profile.ClientId,
            ClientCredentialStyle = ClientCredentialStyle.PostBody,
            Code = callback.Code!,
            CodeVerifier = state.CodeVerifier,
            RedirectUri = state.RedirectUri
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (response.IsError)
        {
            if (response.Exception is OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (response.Exception is CliAuthException safe)
            {
                throw safe;
            }

            throw new CliAuthException(AuthErrorCode.ProtocolFailure, response.Error);
        }

        return await ValidateResponseAsync(profile, provider,
            response.Raw ?? throw new CliAuthException(AuthErrorCode.ProtocolFailure), nonce, started, ct).ConfigureAwait(false);
    }

    private async Task<ValidatedLogin> ValidateResponseAsync(OidcProfile profile, ProviderMetadata provider,
        string raw, string nonce, DateTimeOffset started, CancellationToken ct)
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
            RequireNonce = true,
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

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RevocationOutcome.Unsupported);
    }
}
