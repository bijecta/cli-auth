using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ProtocolSpike;

namespace MicrosoftBaseline;

public sealed class MicrosoftComponentTests
{
    [Fact]
    public void AcceptsSignedBrowserIdentityWithMatchingNonce()
    {
        using var fixture = new TokenFixture();
        var user = fixture.Validate(fixture.CreateToken());
        Assert.Equal("synthetic-subject", user.FindFirst("sub")?.Value);
    }

    [Theory]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("wrong_algorithm")]
    public void RejectsInvalidTokenProperties(string scenario)
    {
        using var fixture = new TokenFixture();
        Assert.ThrowsAny<SecurityTokenException>(() => fixture.Validate(fixture.CreateToken(scenario)));
    }

    [Fact]
    public void RejectsDifferentSigningKey()
    {
        using var fixture = new TokenFixture();
        using var otherRsa = RSA.Create(2048);
        Assert.ThrowsAny<SecurityTokenException>(() => fixture.Validate(
            fixture.CreateToken(), key: new RsaSecurityKey(otherRsa) { KeyId = "spike-key" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-nonce")]
    public void RejectsBrowserNonceMismatch(string? nonce)
    {
        using var fixture = new TokenFixture();
        Assert.ThrowsAny<OpenIdConnectProtocolException>(() => fixture.Validate(fixture.CreateToken(nonce: nonce)));
    }

    [Fact]
    public void AcceptsDeviceIdentityWithoutInventingBrowserNonce()
    {
        using var fixture = new TokenFixture();
        var user = fixture.Validate(fixture.CreateToken(nonce: null), expectedNonce: null);
        Assert.Equal("synthetic-subject", user.FindFirst("sub")?.Value);
    }

    [Fact]
    public void RejectsIncorrectAuthorizedParty()
    {
        using var fixture = new TokenFixture();
        Assert.ThrowsAny<OpenIdConnectProtocolException>(() => fixture.Validate(fixture.CreateToken("wrong_azp")));
    }

    [Fact]
    public async Task DiscoveryManagerIsLazyAndRetrievesMetadataAndKeys()
    {
        using var fixture = new TokenFixture();
        using var handler = new FakeBackchannel((request, _) => Task.FromResult(FakeBackchannel.Json(
            request.RequestUri!.AbsolutePath == "/jwks" ? fixture.PublicJwks() : Discovery())));
        using var http = new HttpClient(handler);
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            TokenFixture.Issuer + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http));
        Assert.Equal(0, handler.Calls);

        var configuration = await manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TokenFixture.Issuer, configuration.Issuer);
        Assert.Single(configuration.SigningKeys);
        Assert.Equal(2, handler.Calls);
        await manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DiscoveryRetrieverAloneDoesNotEnforceOurExactIssuerPolicy()
    {
        using var handler = new FakeBackchannel((_, _) => Task.FromResult(FakeBackchannel.Json(
            JsonSerializer.Serialize(new { issuer = "https://different.example.test" }))));
        using var http = new HttpClient(handler);
        var document = await OpenIdConnectConfigurationRetriever.GetAsync(
            TokenFixture.Issuer + "/.well-known/openid-configuration", http,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(TokenFixture.Issuer, document.Issuer);
        // This is an observed gap: our coordinator must compare the configured issuer.
    }

    [Fact]
    public async Task DefaultRetrieverDoesNotForwardCallerCancellationInEvaluatedVersion()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeBackchannel(async (_, ct) =>
        {
            entered.SetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = OpenIdConnectConfigurationRetriever.GetAsync(
            TokenFixture.Issuer + "/.well-known/openid-configuration", http, cancellation.Token);
        var transportToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        try
        {
            Assert.False(transportToken.IsCancellationRequested);
        }
        finally
        {
            http.CancelPendingRequests();
            await Assert.ThrowsAnyAsync<IOException>(() => operation);
        }
    }

    [Fact]
    public async Task CustomDocumentRetrieverForwardsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeBackchannel(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = OpenIdConnectConfigurationRetriever.GetAsync(
            TokenFixture.Issuer + "/.well-known/openid-configuration", new CancellableRetriever(http), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private sealed class CancellableRetriever(HttpClient http) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancel);
        }
    }

    private static string Discovery() => JsonSerializer.Serialize(new
    {
        issuer = TokenFixture.Issuer,
        authorization_endpoint = TokenFixture.Issuer + "/authorize",
        token_endpoint = TokenFixture.Issuer + "/token",
        jwks_uri = TokenFixture.Issuer + "/jwks",
        response_types_supported = new[] { "code" },
        id_token_signing_alg_values_supported = new[] { "RS256" }
    });
}
