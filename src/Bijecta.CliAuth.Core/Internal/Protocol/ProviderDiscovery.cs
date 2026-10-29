using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.Core;

internal sealed record ProviderMetadata(Uri? AuthorizationEndpoint, Uri TokenEndpoint, Uri JwksUri, string Jwks, IReadOnlyList<SecurityKey> Keys, Uri? DeviceAuthorizationEndpoint = null);
internal sealed class ProviderDiscovery(HttpClient http, TimeProvider time)
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal ProviderMetadata? Metadata;
        internal long Started;
    }

    private readonly ConcurrentDictionary<(SessionKey, LoginFlow), Entry> _entries = new();
    internal async Task<ProviderMetadata> GetAsync(OidcProfile profile, CancellationToken ct, LoginFlow flow = LoginFlow.Browser)
    {
        var entry = _entries.GetOrAdd((SessionKey.ForProfile(profile), flow), _ => new Entry());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Metadata is not null && time.GetElapsedTime(entry.Started) < TimeSpan.FromMinutes(5))
            {
                return entry.Metadata;
            }

            var start = time.GetTimestamp();
            try
            {
                using var response = await http.GetAsync(profile.Issuer.OriginalString.TrimEnd('/') + "/.well-known/openid-configuration", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CliAuthException(AuthErrorCode.DiscoveryFailed);
                }

                using var json = ProtocolJson.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = json.RootElement;
                if (ProtocolJson.Required(root, "issuer") != profile.Issuer.OriginalString)
                {
                    throw new CliAuthException(AuthErrorCode.DiscoveryFailed);
                }

                Uri? authorization = null;
                Uri? device = null;
                if (flow == LoginFlow.Browser)
                {
                    Supports(root, "response_types_supported", "code", required: true);
                    Supports(root, "grant_types_supported", "authorization_code");
                    Supports(root, "code_challenge_methods_supported", "S256");
                    if (root.TryGetProperty("require_pushed_authorization_requests", out var par) && par.GetBoolean())
                    {
                        throw new CliAuthException(AuthErrorCode.UnsupportedCapability);
                    }
                    authorization = Endpoint(profile, RequiredCapability(root, "authorization_endpoint"));
                }
                else
                {
                    Supports(root, "grant_types_supported", "urn:ietf:params:oauth:grant-type:device_code");
                    device = Endpoint(profile, RequiredCapability(root, "device_authorization_endpoint"));
                }
                var token = Endpoint(profile, ProtocolJson.Required(root, "token_endpoint"));
                var jwks = Endpoint(profile, ProtocolJson.Required(root, "jwks_uri"));
                var keys = await LoadKeysAsync(jwks, ct).ConfigureAwait(false);
                entry.Metadata = new ProviderMetadata(authorization, token, jwks, keys.Json, keys.Keys, device);
                entry.Started = start;
                return entry.Metadata;
            }
            catch (CliAuthException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw new CliAuthException(AuthErrorCode.DiscoveryFailed);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    internal async Task<ProviderMetadata> ReloadKeysAsync(ProviderMetadata metadata, CancellationToken ct)
    {
        var keys = await LoadKeysAsync(metadata.JwksUri, ct).ConfigureAwait(false);
        // Keep rotation retries local to this login, never force concurrent transactions to trust a replacement.
        return metadata with
        {
            Jwks = keys.Json,
            Keys = keys.Keys
        };
    }

    private async Task<(string Json, IReadOnlyList<SecurityKey> Keys)> LoadKeysAsync(Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliAuthException(AuthErrorCode.DiscoveryFailed);
        }

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var json = ProtocolJson.Parse(text);
        var set = new JsonWebKeySet(text);
        var keys = new List<SecurityKey>();
        foreach (var key in set.Keys)
        {
            // Never accept symmetric or private keys from a provider document.
            if ((!string.IsNullOrEmpty(key.Use) && key.Use != "sig") || !string.IsNullOrEmpty(key.D) ||
                (key.KeyOps.Count != 0 && !key.KeyOps.Contains("verify")))
            {
                continue;
            }

            if (key.Kty == "RSA" && (string.IsNullOrEmpty(key.Alg) || key.Alg == "RS256") && key.KeySize >= 2048 ||
                key is { Kty: "EC", Crv: "P-256" } && (string.IsNullOrEmpty(key.Alg) || key.Alg == "ES256"))
            {
                keys.Add(key);
            }
        }

        return keys.Count == 0 ? throw new CliAuthException(AuthErrorCode.DiscoveryFailed) : (text, keys.AsReadOnly());
    }

    private static string RequiredCapability(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new CliAuthException(AuthErrorCode.UnsupportedCapability);

    private static void Supports(JsonElement root, string name, string value, bool required = false)
    {
        if (!root.TryGetProperty(name, out var list))
        {
            if (required) { throw new CliAuthException(AuthErrorCode.DiscoveryFailed); }
            return;
        }

        if (list.ValueKind != JsonValueKind.Array || !list.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == value))
        {
            throw new CliAuthException(AuthErrorCode.UnsupportedCapability);
        }
    }

    private static Uri Endpoint(OidcProfile profile, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            value.Contains('#') || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new CliAuthException(AuthErrorCode.DestinationNotAllowed);
        }

        var https = uri.Scheme == Uri.UriSchemeHttps;
        var loopback = profile.AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && ContractValidation.IsLiteralLoopback(uri);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if ((!https && !loopback) || (origin != profile.Issuer.GetLeftPart(UriPartial.Authority) &&
            !profile.AllowedEndpointOrigins.Any(x => x.GetLeftPart(UriPartial.Authority) == origin)))
        {
            throw new CliAuthException(AuthErrorCode.DestinationNotAllowed);
        }

        return uri;
    }
}
