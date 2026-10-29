using System.Security.Cryptography;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Logging.Abstractions;
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
                AuthorizeEndpoint = provider.AuthorizationEndpoint!.AbsoluteUri,
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

        return await new LoginTokenValidator(_time, _discovery).ValidateResponseAsync(profile, provider,
            response.Raw ?? throw new CliAuthException(AuthErrorCode.ProtocolFailure), nonce, started, ct).ConfigureAwait(false);
    }

    public Task<RevocationOutcome> RevokeRefreshTokenAsync(OidcProfile profile, string refreshToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RevocationOutcome.Unsupported);
    }
}
