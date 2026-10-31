using System.Net;
using Duende.IdentityModel.Client;

namespace Bijecta.CliAuth.Core;

// One refresh_token grant per call, never retried here: the coordinator owns retry policy and the session lease.
internal sealed class RefreshTokenProtocol
{
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ProviderDiscovery _discovery;

    internal RefreshTokenProtocol(TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _time = time ?? TimeProvider.System;
        _http = ProtocolHttp.Create(_time, transport);
        _discovery = new ProviderDiscovery(_http, _time);
    }

    internal async Task<ValidatedRefresh> RefreshAsync(OidcProfile profile, string refreshToken, string subject,
        IReadOnlyList<string> grantedScopes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var provider = await _discovery.GetAsync(profile, ct, flow: null).ConfigureAwait(false);
            var started = _time.GetUtcNow();
            // No scope parameter: the refreshed token keeps the originally granted scopes.
            var response = await _http.RequestRefreshTokenAsync(new RefreshTokenRequest
            {
                Address = provider.TokenEndpoint.AbsoluteUri,
                ClientId = profile.ClientId,
                ClientCredentialStyle = ClientCredentialStyle.PostBody,
                RefreshToken = refreshToken
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (response.IsError)
            {
                throw Classify(response);
            }
            if (response.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new CliAuthException(AuthErrorCode.ProtocolFailure);
            }

            return await new LoginTokenValidator(_time, _discovery).ValidateRefreshAsync(profile, provider,
                response.Raw ?? throw new CliAuthException(AuthErrorCode.ProtocolFailure), subject, grantedScopes,
                started, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (CliAuthException) { throw; }
        catch
        {
            // Raw parser and token-handler exceptions may contain credentials.
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }
    }

    private static Exception Classify(TokenResponse response)
    {
        switch (response.Exception)
        {
            case OperationCanceledException canceled: return canceled;
            // Transport failures and timeouts arrive here already classified by ProtocolHttp.
            case CliAuthException safe: return safe;
            // Anything else is a response that could not be parsed.
            case not null: return new CliAuthException(AuthErrorCode.ProtocolFailure);
        }

        // ADR 0009: invalid_grant ends the session; transient provider failures keep it; anything else is a typed
        // protocol error carrying only the provider's error code.
        var error = response.ErrorType == ResponseErrorType.Protocol ? response.Error : null;
        if (error == "invalid_grant")
        {
            return new CliAuthException(AuthErrorCode.ReauthenticationRequired, error);
        }
        if (error == "temporarily_unavailable" || (int)response.HttpStatusCode >= 500)
        {
            return new CliAuthException(AuthErrorCode.NetworkFailure, error);
        }
        return new CliAuthException(AuthErrorCode.ProtocolFailure, error);
    }
}
