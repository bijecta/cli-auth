using System.Net;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class DiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("issuer", "https://other.example.test", AuthErrorCode.DiscoveryFailed)]
    [InlineData("issuer", BrowserFixture.Issuer + "/", AuthErrorCode.DiscoveryFailed)]
    [InlineData("authorization_endpoint", "http://issuer.example.test/authorize", AuthErrorCode.DestinationNotAllowed)]
    [InlineData("token_endpoint", "https://other.example.test/token", AuthErrorCode.DestinationNotAllowed)]
    [InlineData("jwks_uri", "https://other.example.test/keys", AuthErrorCode.DestinationNotAllowed)]
    public async Task UntrustedMetadataFailsBeforeBrowserOrTokenRequest(string field, string value, AuthErrorCode code)
    {
        using var f = new BrowserFixture();
        f.ChangeMetadata = m =>
        {
            m[field] = value;
            return m;
        };
        var error = await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(0, f.BrowserCalls);
        Assert.Equal(0, f.TokenCalls);
    }

    [Theory]
    [InlineData("response_types_supported", "token")]
    [InlineData("grant_types_supported", "client_credentials")]
    [InlineData("code_challenge_methods_supported", "plain")]
    public async Task ExplicitIncompatibleCapabilitiesAreRejected(string field, string value)
    {
        using var f = new BrowserFixture();
        f.ChangeMetadata = m =>
        {
            m[field] = new[]
            {
                value
            };
            return m;
        };
        Assert.Equal(AuthErrorCode.UnsupportedCapability, (await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Fact]
    public async Task MissingOptionalCapabilitiesDoNotBlockSupportedFlow()
    {
        using var f = new BrowserFixture();
        f.ChangeMetadata = m =>
        {
            m.Remove("code_challenge_methods_supported");
            m.Remove("grant_types_supported");
            return m;
        };
        await f.Auth().LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal("S256", f.Front!["code_challenge_method"]);
    }

    [Fact]
    public async Task RequiredParIsUnsupportedBeforeBrowser()
    {
        using var f = new BrowserFixture();
        f.ChangeMetadata = m =>
        {
            m["require_pushed_authorization_requests"] = true;
            return m;
        };
        Assert.Equal(AuthErrorCode.UnsupportedCapability, (await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct))).ErrorCode);
        Assert.Equal(0, f.BrowserCalls);
    }

    [Fact]
    public async Task ExtraOriginRequiresExplicitTrustAndChangesSessionIsolation()
    {
        using var f = new BrowserFixture();
        f.ChangeMetadata = m =>
        {
            m["authorization_endpoint"] = "https://login.example.test/authorize";
            return m;
        };
        var profile = f.Profile(origins: [new Uri("https://login.example.test")]);
        Assert.NotEqual(SessionKey.ForProfile(f.Profile()), SessionKey.ForProfile(profile));
        await f.Auth(profile: profile).LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(1, f.TokenCalls);
    }

    [Fact]
    public async Task DiscoveryAndKeysAreLazyAndExpireAfterFiveMinutes()
    {
        using var f = new BrowserFixture();
        var auth = f.Auth();
        Assert.Equal(0, f.MetadataCalls);
        await auth.GetStatusAsync("work", Ct);
        Assert.Equal(0, f.MetadataCalls);
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(1, f.MetadataCalls);
        Assert.Equal(1, f.KeyCalls);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await auth.LoginAsync("work", new LoginOptions(), Ct);
        Assert.Equal(2, f.MetadataCalls);
        Assert.Equal(2, f.KeyCalls);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    [InlineData("invalid_json")]
    public async Task InvalidDiscoveryResponseFailsClosed(string scenario)
    {
        using var f = new BrowserFixture();
        f.Override = (_, _) => Task.FromResult(scenario switch
        {
            "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example.test") } },
            "oversize" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 1024 * 1024 + 1)) },
            "duplicate" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"issuer\":\"one\",\"issuer\":\"two\"}") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }
        });
        await Assert.ThrowsAsync<CliAuthException>(() => f.Auth().LoginAsync("work", new LoginOptions(), Ct));
        Assert.Equal(0, f.BrowserCalls);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact]
    public async Task DeviceSelectionIsUnsupportedWithoutDiscoveryOrBrowserFallback()
    {
        using var f = new BrowserFixture();
        Assert.Equal(AuthErrorCode.UnsupportedCapability, (await Assert.ThrowsAsync<CliAuthException>(() => f.Protocol().LoginAsync(f.Profile(), new LoginOptions(LoginFlow.Device, (_, _) => Task.CompletedTask), f, Ct))).ErrorCode);
        Assert.Equal(0, f.MetadataCalls);
        Assert.Equal(0, f.BrowserCalls);
    }
}
