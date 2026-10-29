using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bijecta.CliAuth.Core;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.ProtocolTests;

internal sealed class BrowserFixture : HttpMessageHandler, ISystemBrowser
{
    internal const string Issuer = "https://issuer.example.test/realm";
    internal const string ClientId = "test-cli";
    internal readonly DeviceTestClock Time = new(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _rotated = RSA.Create(2048);
    private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly MemoryTokenStore Store = new();
    internal Dictionary<string, string>? Front;
    internal Dictionary<string, string>? Exchange;
    internal int MetadataCalls, KeyCalls, TokenCalls, BrowserCalls, DeviceCalls;
    internal bool Device;
    internal Dictionary<string, string>? DeviceRequest;
    internal Func<Dictionary<string, object>, Dictionary<string, object>>? ChangeDevice;
    internal Func<Dictionary<string, object>, Dictionary<string, object>>? ChangeMetadata;
    internal Func<Dictionary<string, object>, Dictionary<string, object>>? ChangeTokens;
    internal Func<Uri, CancellationToken, Task>? OnBrowser;
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override;
    internal Func<object, object>? ChangeJwks;
    internal Func<CancellationToken, Task<HttpResponseMessage>>? OnTokenRequest;
    internal Func<CancellationToken, Task<HttpResponseMessage>>? OnDeviceRequest;
    internal string Scenario = "valid";
    internal Uri? LastCallback;
    internal OidcProfile Profile(int port = 0, IEnumerable<Uri>? origins = null) => new("protocol-tests", "work", new Uri(Issuer), ClientId, ["profile"], PersistenceMode.MemoryOnly, new BrowserCallbackOptions(port), allowedEndpointOrigins: origins);
    internal BrowserAuthenticationProtocol Protocol() => new(Time, this);
    internal CliAuthenticator Auth(IAuthenticationProtocol? protocol = null, OidcProfile? profile = null) => new([profile ?? Profile()], Store, protocol ?? Protocol(), browser: this, timeProvider: Time);
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        BrowserCalls++;
        Front = Query(uri.Query);
        LastCallback = new Uri(Front["redirect_uri"]);
        if (OnBrowser is not null)
        {
            await OnBrowser(uri, cancellationToken);
            return;
        }

        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        using var reply = await client.GetAsync(Callback(), cancellationToken);
        if (reply.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException("Synthetic callback rejected.");
        }
    }

    internal Uri Callback(string? query = null) => new(LastCallback!.AbsoluteUri + "?" + (query ?? "state=" + Uri.EscapeDataString(Front!["state"]) + "&code=synthetic-code&iss=" + Uri.EscapeDataString(Issuer)));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Override is not null)
        {
            return await Override(request, ct);
        }

        var uri = request.RequestUri!.AbsoluteUri;
        if (uri.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            MetadataCalls++;
            var metadata = new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["jwks_uri"] = Issuer + "/keys",
                ["response_types_supported"] = new[]
                {
                    "code"
                },
                ["grant_types_supported"] = new[]
                {
                    "authorization_code"
                },
                ["code_challenge_methods_supported"] = new[]
                {
                    "S256"
                }
            };
            if (Device)
            {
                metadata["device_authorization_endpoint"] = Issuer + "/device";
                metadata["grant_types_supported"] = new[] { "authorization_code", "urn:ietf:params:oauth:grant-type:device_code" };
            }
            return Json(ChangeMetadata?.Invoke(metadata) ?? metadata);

        }

        if (uri.EndsWith("/keys", StringComparison.Ordinal))
        {
            KeyCalls++;
            if (Scenario == "es256")
            {
                var p = _ec.ExportParameters(false);
                return Json(new
                {
                    keys = new[] { new { kty = "EC", kid = "ec", crv = "P-256", use = "sig", alg = "ES256",
                    x = Base64UrlEncoder.Encode(p.Q.X!), y = Base64UrlEncoder.Encode(p.Q.Y!) } }
                });
            }
            var keyDocument = Jwks(Scenario == "rotation" && KeyCalls > 1 ? _rotated : _rsa, Scenario == "rotation" && KeyCalls > 1 ? "rotated" : "key");
            return Json(ChangeJwks?.Invoke(keyDocument) ?? keyDocument);
        }

        if (uri.EndsWith("/device", StringComparison.Ordinal))
        {
            DeviceCalls++;
            DeviceRequest = Query(await request.Content!.ReadAsStringAsync(ct));
            if (OnDeviceRequest is not null) { return await OnDeviceRequest(ct); }
            var device = new Dictionary<string, object>
            {
                ["device_code"] = "secret-device-code",
                ["user_code"] = "USER-CODE",
                ["verification_uri"] = Issuer + "/verify",
                ["verification_uri_complete"] = Issuer + "/verify?user_code=USER-CODE",
                ["expires_in"] = 600
            };
            return Json(ChangeDevice?.Invoke(device) ?? device);
        }

        if (uri.EndsWith("/token", StringComparison.Ordinal))

        {
            TokenCalls++;
            Exchange = Query(await request.Content!.ReadAsStringAsync(ct));
            if (OnTokenRequest is not null) { return await OnTokenRequest(ct); }
            var token = new Dictionary<string, object>
            {
                ["access_token"] = "synthetic-access",
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = IdToken(),
                ["refresh_token"] = "synthetic-refresh"
            };
            return Json(ChangeTokens?.Invoke(token) ?? token);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string IdToken()
    {
        var claims = new List<Claim>();
        if (Scenario != "missing_sub")
        {
            claims.Add(new Claim("sub", "test-subject"));
        }

        if (Scenario != "missing_nonce" && Exchange?.GetValueOrDefault("grant_type") != "urn:ietf:params:oauth:grant-type:device_code")
        {
            claims.Add(new Claim("nonce", Scenario == "wrong_nonce" ? "wrong" : Front!["nonce"]));
        }

        if (Scenario is "wrong_azp" or "valid_azp")
        {
            claims.Add(new Claim("azp", Scenario == "wrong_azp" ? "wrong" : ClientId));
        }

        if (Scenario == "wrong_at_hash")
        {
            claims.Add(new Claim("at_hash", Base64UrlEncoder.Encode(new byte[16])));
        }

        if (Scenario == "valid_at_hash")
        {
            claims.Add(new Claim("at_hash", Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("synthetic-access"))[..16])));
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var key = new RsaSecurityKey(Scenario == "rotation" || Scenario == "bad_signature" || Scenario == "unknown_kid" ? _rotated : _rsa)
        {
            KeyId = Scenario == "rotation" || Scenario == "unknown_kid" ? "rotated" : "key"
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Scenario == "wrong_issuer" ? "https://other.example.test" : Issuer,
            Audience = Scenario == "wrong_audience" ? "wrong" : Scenario == "audience_slash" ? ClientId + "/" : ClientId,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.AddMinutes(-10),
            NotBefore = Scenario == "future_nbf" ? now.AddMinutes(10) : now.AddMinutes(-10),
            Expires = Scenario == "expired" ? now.AddMinutes(-2) : now.AddMinutes(20),
            SigningCredentials = Scenario == "unsigned" ? null : new SigningCredentials(key, Scenario == "wrong_algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256)
        };
        if (Scenario == "es256")
        {
            descriptor.SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_ec) { KeyId = "ec" }, SecurityAlgorithms.EcdsaSha256);
        }
        if (Scenario == "missing_azp")
        {
            descriptor.Audiences.Add("second-client");
        }
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    internal static object Jwks(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    kid,
                    use = "sig",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(p.Modulus!),
                    e = Base64UrlEncoder.Encode(p.Exponent!)
                }
            }
        };
    }

    internal static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    internal static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&').Where(x => x.Length > 0).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')), StringComparer.Ordinal);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
            _rotated.Dispose();
            _ec.Dispose();
        }

        base.Dispose(disposing);
    }
}
