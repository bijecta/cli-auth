using System.Net;
using System.Text.Json;
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
            // The provider has answered and may already have rotated the refresh token. Finish validation even if the
            // caller cancels or the coordinator's deadline passes, so the result can be committed (ADR 0009).
            if (response.IsError)
            {
                throw Classify(response);
            }
            if (response.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new CliAuthException(AuthErrorCode.ProtocolFailure);
            }

            // Only a signing-key reload can wait here; it gets its own short bound while the session lease is held.
            using var validation = new CancellationTokenSource(TimeSpan.FromSeconds(10), _time);
            return await new LoginTokenValidator(_time, _discovery).ValidateRefreshAsync(profile, provider,
                response.Raw ?? throw new CliAuthException(AuthErrorCode.ProtocolFailure), subject, grantedScopes,
                started, validation.Token).ConfigureAwait(false);
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
        var error = ProviderError(response);
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

    // Read whatever the status: the token client treats only 400 as an OAuth error, but RFC 6749 allows 401 for
    // invalid_client, and some providers send invalid_grant with 401 or 403.
    private static string? ProviderError(TokenResponse response) =>
        response.Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.String ? error.GetString() : null;
}
