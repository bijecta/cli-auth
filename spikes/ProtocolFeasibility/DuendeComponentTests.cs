using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ProtocolSpike;

namespace ProtocolFeasibility;

// Characterization probes, not a production authentication implementation.
// These must be compiled and executed against the pinned NuGet before conclusions.
public sealed class DuendeComponentTests
{
    [Fact]
    public void RunsTheVersionSelectedByTheMatrix()
    {
        var expected = typeof(DuendeComponentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "EvaluatedDuendeVersion").Value;
        var actual = typeof(OidcClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConstructionDoesNotFetchDiscovery()
    {
        using var handler = new FakeBackchannel((_, _) => throw new InvalidOperationException("Unexpected request"));
        _ = new OidcClient(new OidcClientOptions
        {
            Authority = TokenFixture.Issuer,
            ClientId = TokenFixture.ClientId,
            Scope = "openid",
            RedirectUri = "http://127.0.0.1:43821/callback/",
            BackchannelHandler = handler
        });
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreparesS256PkceAndAcceptsAnExplicitNonce()
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((_, _) => throw new InvalidOperationException("Unexpected request"));
        var client = Client(fixture, handler);
        var state = await client.PrepareLoginAsync(
            new Parameters { { "nonce", TokenFixture.Nonce } }, TestContext.Current.CancellationToken);
        var query = Query(new Uri(state.StartUrl).Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(state.CodeVerifier))),
            query["code_challenge"]);
        Assert.Equal(TokenFixture.Nonce, query["nonce"]);
        Assert.Equal(state.State, query["state"]);
        Assert.Equal("http://127.0.0.1:43821/callback/", query["redirect_uri"]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BrowserPreparationNeedsOurExplicitNoncePolicy()
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((_, _) => throw new InvalidOperationException("Unexpected request"));
        var state = await Client(fixture, handler).PrepareLoginAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("nonce", Query(new Uri(state.StartUrl).Query).Keys);
    }

    [Fact]
    public async Task MissingIdTokenNeedsOurExplicitOidcResponseCheck()
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((_, _) => Task.FromResult(FakeBackchannel.Json(
            """{"access_token":"synthetic-access-token","token_type":"Bearer","expires_in":300}""")));
        var client = Client(fixture, handler);
        var state = await client.PrepareLoginAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.ProcessResponseAsync(
            state.RedirectUri + "?code=synthetic-code&state=" + Uri.EscapeDataString(state.State), state,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Null(result.IdentityToken);
        // The production OIDC adapter must reject this result before session commit.
    }

    [Fact]
    public async Task RejectsForgedCallbackStateWithoutRedeemingCode()
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((_, _) => throw new InvalidOperationException("Unexpected request"));
        var client = Client(fixture, handler);
        var state = await client.PrepareLoginAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.ProcessResponseAsync(
            state.RedirectUri + "?code=synthetic-code&state=forged", state,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong_issuer", false)]
    [InlineData("wrong_audience", false)]
    [InlineData("unsigned", false)]
    [InlineData("wrong_algorithm", false)]
    [InlineData("expired", false)]
    public async Task CanBridgeBrowserResponseToMicrosoftValidation(string scenario, bool success)
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((_, _) => Task.FromResult(FakeBackchannel.Json(
            JsonSerializer.Serialize(new
            {
                access_token = TokenFixture.AccessToken,
                token_type = "Bearer",
                expires_in = 300,
                id_token = fixture.CreateToken(scenario)
            }))));
        var client = Client(fixture, handler);
        var state = await client.PrepareLoginAsync(
            new Parameters { { "nonce", TokenFixture.Nonce } }, TestContext.Current.CancellationToken);
        var result = await client.ProcessResponseAsync(
            state.RedirectUri + "?code=synthetic-code&state=" + Uri.EscapeDataString(state.State), state,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(!success, result.IsError);
        Assert.Equal(1, handler.Calls);
        if (success)
        {
            Assert.Equal("synthetic-subject", result.User.FindFirst("sub")?.Value);
        }
    }

    [Fact]
    public async Task DeviceHelpersExposePendingAndLeavePollingToTheCaller()
    {
        var requests = new List<Dictionary<string, string>>();
        using var handler = new FakeBackchannel(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            requests.Add(Query(body));
            return request.RequestUri!.AbsolutePath == "/device" ? FakeBackchannel.Json(
                """{"device_code":"synthetic-device-code","user_code":"DEMO-CODE","verification_uri":"https://issuer.example.test/verify","expires_in":600,"interval":5}""") :
                FakeBackchannel.Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);
        });
        using var http = new HttpClient(handler);
        var authorization = await http.RequestDeviceAuthorizationAsync(new DeviceAuthorizationRequest
        {
            Address = TokenFixture.Issuer + "/device",
            ClientId = TokenFixture.ClientId,
            Scope = "openid"
        }, TestContext.Current.CancellationToken);
        Assert.False(authorization.IsError);
        Assert.Equal("DEMO-CODE", authorization.UserCode);
        Assert.Equal(5, authorization.Interval);
        var pending = await http.RequestDeviceTokenAsync(new DeviceTokenRequest
        {
            Address = TokenFixture.Issuer + "/token",
            ClientId = TokenFixture.ClientId,
            DeviceCode = Assert.IsType<string>(authorization.DeviceCode)
        }, TestContext.Current.CancellationToken);
        Assert.True(pending.IsError);
        Assert.Equal("authorization_pending", pending.Error);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", requests[1]["grant_type"]);
        Assert.DoesNotContain("client_secret", requests[1].Keys);
    }

    [Fact]
    public async Task DeviceRequestPropagatesCancellation()
    {
        using var handler = new FakeBackchannel(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        // Some helpers return cancellation in a response rather than throwing.
        // Characterize either representation; the production adapter must normalize it.
        try
        {
            var response = await http.RequestDeviceAuthorizationAsync(new DeviceAuthorizationRequest
            {
                Address = TokenFixture.Issuer + "/device",
                ClientId = TokenFixture.ClientId,
                Scope = "openid"
            }, cancellation.Token);
            Assert.True(response.IsError);
            Assert.IsAssignableFrom<OperationCanceledException>(response.Exception);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TokenPostIsNotAutomaticallyRetriedOnServerFailure()
    {
        using var handler = new FakeBackchannel((_, _) => Task.FromResult(
            FakeBackchannel.Json("""{"error":"server_error"}""", HttpStatusCode.InternalServerError)));
        using var http = new HttpClient(handler);
        var result = await http.RequestDeviceTokenAsync(new DeviceTokenRequest
        {
            Address = TokenFixture.Issuer + "/token",
            ClientId = TokenFixture.ClientId,
            DeviceCode = "synthetic-device-code"
        }, TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshHelperReturnsReplacementOrAbsenceForOurCoordinator(bool replacesRefreshToken)
    {
        using var handler = new FakeBackchannel((_, _) => Task.FromResult(FakeBackchannel.Json(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["access_token"] = TokenFixture.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["refresh_token"] = replacesRefreshToken ? "synthetic-new-refresh" : null
            }.Where(pair => pair.Value is not null).ToDictionary()))));
        using var http = new HttpClient(handler);
        var result = await http.RequestRefreshTokenAsync(new RefreshTokenRequest
        {
            Address = TokenFixture.Issuer + "/token",
            ClientId = TokenFixture.ClientId,
            RefreshToken = "synthetic-old-refresh"
        }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal(replacesRefreshToken ? "synthetic-new-refresh" : null, result.RefreshToken);
        Assert.Equal(1, handler.Calls);
    }

    private static OidcClient Client(TokenFixture fixture, HttpMessageHandler handler) => new(new OidcClientOptions
    {
        Authority = TokenFixture.Issuer,
        ClientId = TokenFixture.ClientId,
        Scope = "openid",
        RedirectUri = "http://127.0.0.1:43821/callback/",
        LoadProfile = false,
        FilterClaims = false,
        StateLength = 32,
        DisablePushedAuthorization = true,
        BackchannelHandler = handler,
        ProviderInformation = new ProviderInformation
        {
            IssuerName = TokenFixture.Issuer,
            AuthorizeEndpoint = TokenFixture.Issuer + "/authorize",
            TokenEndpoint = TokenFixture.Issuer + "/token",
            KeySet = new Duende.IdentityModel.Jwk.JsonWebKeySet(fixture.PublicJwks())
        },
        Policy = new Policy { RequireIdentityTokenSignature = true },
        IdentityTokenValidator = new MicrosoftValidator(fixture)
    });

    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&')
        .Where(part => part.Length > 0)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
            part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));

    private sealed class MicrosoftValidator(TokenFixture fixture) : IIdentityTokenValidator
    {
        public Task<IdentityTokenValidationResult> ValidateAsync(
            string identityToken, OidcClientOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(new IdentityTokenValidationResult
                {
                    User = fixture.Validate(identityToken),
                    SignatureAlgorithm = SecurityAlgorithms.RsaSha256
                });
            }
            catch (Exception error) when (error is SecurityTokenException or OpenIdConnectProtocolException)
            {
                return Task.FromResult(new IdentityTokenValidationResult { Error = "identity_validation_failed" });
            }
        }
    }
}
