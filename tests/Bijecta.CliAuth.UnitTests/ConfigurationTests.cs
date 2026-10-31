using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ScopeConfigurationIsDefensiveDistinctAndCaseSensitive()
    {
        var input = new List<string> { "api.read", "api.read", "API.read" };
        var profile = CoreTestData.Profile(scopes: input);
        input.Add("admin");
        Assert.Equal(["API.read", "api.read", "openid"], profile.Scopes);
        Assert.DoesNotContain("profile", profile.Scopes);
        Assert.DoesNotContain("offline_access", profile.Scopes);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)profile.Scopes).Add("admin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("api read")]
    [InlineData("api\nread")]
    [InlineData("api\\read")]
    [InlineData("api\"read")]
    [InlineData("café")]
    public void RejectsMalformedScopeTokens(string scope)
    {
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(scopes: [scope]));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Theory]
    [InlineData("http://issuer.example.test")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://user:password@issuer.example.test")]
    [InlineData("https://issuer.example.test?query=value")]
    [InlineData("https://issuer.example.test?")]
    [InlineData("https://issuer.example.test#fragment")]
    [InlineData("ftp://issuer.example.test")]
    public void RejectsUnsafeIssuerConfiguration(string issuer)
    {
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(issuer: issuer));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Fact]
    public void LoopbackDevelopmentExceptionRequiresLiteralAddressAndExplicitOptIn()
    {
        var profile = CoreTestData.Profile(issuer: "http://127.0.0.1:8080", allowLoopbackHttp: true);
        Assert.Equal("http://127.0.0.1:8080", profile.Issuer.OriginalString);
        Assert.Throws<CliAuthException>(() => CoreTestData.Profile(issuer: "http://localhost:8080", allowLoopbackHttp: true));
        Assert.Throws<CliAuthException>(() => CoreTestData.Profile(issuer: "http://10.0.0.1:8080", allowLoopbackHttp: true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void RejectsOutOfRangeExpiryMargin(int seconds)
    {
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(margin: TimeSpan.FromSeconds(seconds)));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public void RejectsInvalidNamespaceIdentifiersWithoutEchoingInput(string application)
    {
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(application: application));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
        Assert.Equal("Authentication configuration is invalid.", error.Message);
    }

    [Fact]
    public void RejectsInvalidUtf16RatherThanHashingReplacementCharacters()
    {
        var identifier = new string((char)0xd800, 1);
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(application: identifier));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Theory]
    [InlineData(-1, "/callback/")]
    [InlineData(65536, "/callback/")]
    [InlineData(0, "callback/")]
    [InlineData(0, "/callback")]
    [InlineData(0, "/../callback/")]
    [InlineData(0, "/%2fcallback/")]
    [InlineData(0, "//callback/")]
    [InlineData(0, "/callback/?query")]
    public void RejectsAmbiguousCallbackSettings(int port, string path)
    {
        Assert.Throws<CliAuthException>(() => new BrowserCallbackOptions(port, path));
    }

    [Fact]
    public void FlowSelectionRequiresOnlyItsOwnInteractionSettings()
    {
        var browser = new LoginOptions();
        Assert.Equal(LoginFlow.Browser, browser.Flow);
        Assert.Null(browser.DeviceAuthorizationHandler);
        Assert.Throws<CliAuthException>(() => new LoginOptions(LoginFlow.Device));
        Assert.Throws<CliAuthException>(() => new LoginOptions(LoginFlow.Browser, Present));
        Assert.Throws<CliAuthException>(() => new LoginOptions(LoginFlow.Device, Present, loginHint: "account"));
        Assert.Throws<CliAuthException>(() => new LoginOptions(LoginFlow.Device, Present, forceReauthentication: true));
        Assert.Throws<CliAuthException>(() => new LoginOptions((LoginFlow)99));
        Assert.Equal(LoginFlow.Device, new LoginOptions(LoginFlow.Device, Present).Flow);
    }

    [Fact]
    public void DefaultsRequireProtectedStorageWithoutSelectingAnOs()
    {
        var profile = new OidcProfile("sample", "work", new Uri("https://issuer.example.test"), "client");
        Assert.Equal(PersistenceMode.Protected, profile.Persistence);
        Assert.Equal("127.0.0.1", profile.Callback.Address);
        Assert.Equal(0, profile.Callback.Port);
        Assert.Equal("/callback/", profile.Callback.Path);
        Assert.Equal(TimeSpan.FromSeconds(60), profile.ExpiryMargin);
        Assert.Equal(TimeSpan.FromMinutes(5), profile.RefreshAhead);
    }

    [Theory]
    [InlineData(0, 59)]
    [InlineData(120, 119)]
    [InlineData(60, 1801)]
    public void RejectsAnEarlyRefreshMarginOutsideTheExpiryMarginAndThirtyMinutes(int marginSeconds, int aheadSeconds)
    {
        var error = Assert.Throws<CliAuthException>(() => CoreTestData.Profile(
            margin: marginSeconds == 0 ? null : TimeSpan.FromSeconds(marginSeconds), refreshAhead: TimeSpan.FromSeconds(aheadSeconds)));
        Assert.Equal(AuthErrorCode.InvalidConfiguration, error.ErrorCode);
    }

    [Fact]
    public void AnEarlyRefreshMarginEqualToTheExpiryMarginOrThirtyMinutesIsAccepted()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CoreTestData.Profile(refreshAhead: TimeSpan.FromSeconds(60)).RefreshAhead);
        Assert.Equal(TimeSpan.FromMinutes(30), CoreTestData.Profile(refreshAhead: TimeSpan.FromMinutes(30)).RefreshAhead);
        // The default early margin is never below the largest allowed expiry margin.
        Assert.Equal(TimeSpan.FromMinutes(5), CoreTestData.Profile(margin: TimeSpan.FromSeconds(300)).RefreshAhead);
    }

    private static Task Present(DeviceAuthorizationPrompt prompt, CancellationToken ct) => Task.CompletedTask;
}
