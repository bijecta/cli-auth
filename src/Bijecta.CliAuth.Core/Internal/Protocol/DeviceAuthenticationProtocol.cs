using System.Net;
using System.Text.Json;
using Duende.IdentityModel.Client;

namespace Bijecta.CliAuth.Core;

internal sealed class DeviceAuthenticationProtocol
{
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ProviderDiscovery _discovery;

    internal DeviceAuthenticationProtocol(TimeProvider? time = null, HttpMessageHandler? transport = null)
    {
        _time = time ?? TimeProvider.System;
        _http = ProtocolHttp.Create(_time, transport, distinguishTimeouts: true);
        _discovery = new(_http, _time);
    }

    internal async Task<ValidatedLogin> LoginAsync(OidcProfile profile, LoginOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await LoginCoreAsync(profile, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CliAuthException) { throw; }
        catch (ProtocolRequestTimeoutException) { throw new CliAuthException(AuthErrorCode.NetworkFailure); }
        catch { throw new CliAuthException(AuthErrorCode.ProtocolFailure); }
    }

    private async Task<ValidatedLogin> LoginCoreAsync(OidcProfile profile, LoginOptions options, CancellationToken ct)
    {
        var provider = await _discovery.GetAsync(profile, ct, LoginFlow.Device).ConfigureAwait(false);
        var started = _time.GetUtcNow();
        var timestamp = _time.GetTimestamp();
        var response = await _http.RequestDeviceAuthorizationAsync(new DeviceAuthorizationRequest
        {
            Address = provider.DeviceAuthorizationEndpoint!.AbsoluteUri,
            ClientId = profile.ClientId,
            ClientCredentialStyle = ClientCredentialStyle.PostBody,
            Scope = string.Join(' ', profile.Scopes)
        }, ct).ConfigureAwait(false);
        using var authorizationReply = response.HttpResponse;
        CheckTransport(response, ct);
        using var json = ParseResponse(response);
        if (response.IsError) { throw Failure(response.Error); }
        if (response.HttpStatusCode != HttpStatusCode.OK) { throw Failure(); }

        var root = json.RootElement;
        var deviceCode = ProtocolJson.Required(root, "device_code");
        var userCode = ProtocolJson.Required(root, "user_code");
        var lifetime = PositiveSeconds(root, "expires_in");
        var interval = root.TryGetProperty("interval", out _) ? PositiveSeconds(root, "interval") : TimeSpan.FromSeconds(5);
        var verification = VerificationUri(profile, ProtocolJson.Required(root, "verification_uri"));
        var complete = root.TryGetProperty("verification_uri_complete", out _) ?
            VerificationUri(profile, ProtocolJson.Required(root, "verification_uri_complete")) : null;
        var prompt = new DeviceAuthorizationPrompt(userCode, verification, started.Add(lifetime), complete, profile.AllowLoopbackHttp);

        // Start the provider deadline before invoking host code; authorization response latency counts too.
        var remaining = lifetime - _time.GetElapsedTime(timestamp);
        if (remaining <= TimeSpan.Zero) { throw new CliAuthException(AuthErrorCode.LoginTimedOut); }
        using var deadline = new CancellationTokenSource(remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var transaction = linked.Token;
        try
        {
            EnsureActive();
            await options.DeviceAuthorizationHandler!(prompt, transaction).WaitAsync(transaction).ConfigureAwait(false);
            while (true)
            {
                EnsureActive();
                await Task.Delay(interval, _time, transaction).ConfigureAwait(false);
                EnsureActive();
                var pollStarted = _time.GetUtcNow();
                var tokens = await _http.RequestDeviceTokenAsync(new DeviceTokenRequest
                {
                    Address = provider.TokenEndpoint.AbsoluteUri,
                    ClientId = profile.ClientId,
                    ClientCredentialStyle = ClientCredentialStyle.PostBody,
                    DeviceCode = deviceCode
                }, transaction).ConfigureAwait(false);
                using var tokenReply = tokens.HttpResponse;
                EnsureActive();
                if (tokens.Exception is ProtocolRequestTimeoutException)
                {
                    // Saturate safely: the provider deadline will stop us before a longer wait completes.
                    interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, int.MaxValue));
                    continue;
                }
                CheckTransport(tokens, transaction);
                using var tokenJson = ParseResponse(tokens);
                if (!tokens.IsError)
                {
                    if (tokens.HttpStatusCode != HttpStatusCode.OK) { throw Failure(); }
                    return await new LoginTokenValidator(_time, _discovery).ValidateResponseAsync(
                        profile, provider, tokens.Raw!, nonce: null, pollStarted, transaction).ConfigureAwait(false);
                }
                // Only real OAuth error responses may continue polling; redirects/5xx are terminal.
                if (tokens.HttpStatusCode != HttpStatusCode.BadRequest) { throw Failure(tokens.Error); }
                switch (ProtocolJson.Required(tokenJson.RootElement, "error"))
                {
                    case "authorization_pending": break;
                    case "slow_down":
                        interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds + 5000, int.MaxValue));
                        break;
                    default: throw Failure(tokens.Error);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new CliAuthException(AuthErrorCode.LoginTimedOut);
        }

        void EnsureActive()
        {
            transaction.ThrowIfCancellationRequested();
            if (_time.GetElapsedTime(timestamp) >= lifetime)
            {
                throw new CliAuthException(AuthErrorCode.LoginTimedOut);
            }
        }
    }

    private static void CheckTransport(ProtocolResponse response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.Exception is ProtocolRequestTimeoutException) { throw new ProtocolRequestTimeoutException(); }
        if (response.Exception is CliAuthException safe) { throw safe; }
        if (response.Exception is not null) { throw Failure(); }
    }

    private static JsonDocument ParseResponse(ProtocolResponse response) =>
        ProtocolJson.Parse(response.Raw ?? throw Failure());

    private static CliAuthException Failure(string? error = null) => new(error switch
    {
        "access_denied" => AuthErrorCode.AccessDenied,
        "expired_token" => AuthErrorCode.LoginTimedOut,
        _ => AuthErrorCode.ProtocolFailure
    }, error);

    private static TimeSpan PositiveSeconds(JsonElement root, string name)
    {
        // Bound timer inputs as well as HTTP bodies; fractional/string/overflow values fail closed.
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var seconds) ||
            seconds <= 0 || seconds > int.MaxValue / 1000)
        {
            throw Failure();
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static Uri VerificationUri(OidcProfile profile, string value)
    {
        // Apply the same explicit origin trust as other provider-selected destinations.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.GetLeftPart(UriPartial.Authority) != profile.Issuer.GetLeftPart(UriPartial.Authority) &&
             !profile.AllowedEndpointOrigins.Any(x => x.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority))))
        {
            throw new CliAuthException(AuthErrorCode.DestinationNotAllowed);
        }
        return uri;
    }
}
