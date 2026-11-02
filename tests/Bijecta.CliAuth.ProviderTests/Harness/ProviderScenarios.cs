using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>
/// The standard scenarios behind each row of the compatibility table. A provider adds a sealed subclass naming its
/// <see cref="ProviderDefinition"/> and <see cref="ILoginPage"/>; every scenario then runs, or skips with a stated reason
/// (provider not configured, or a capability the definition declares absent). Only the public authenticator API is used.
/// </summary>
public abstract class ProviderScenarios(BrowserHost host, ITestOutputHelper output)
{
    private const string ProfileName = "work";

    protected ITestOutputHelper Output => output;
    protected abstract ProviderDefinition Definition { get; }
    protected abstract ILoginPage LoginPage { get; }

    /// <summary>
    /// The provider's settings. Hosted providers read environment variables under the skip policy; a provider whose
    /// fixture creates its own server and clients (such as a self-hosted one) returns the fixture's settings instead.
    /// </summary>
    protected virtual ProviderSettings ResolveSettings() => ProviderEnvironment.Require(Definition);

    /// <summary>The profile for one client. Providers override this for options such as allowed endpoint origins.</summary>
    protected virtual OidcProfile CreateProfile(ProviderSettings settings, string clientId) =>
        new("cliauth-provider-tests", ProfileName, settings.Issuer, clientId, settings.Scopes, PersistenceMode.MemoryOnly,
            new BrowserCallbackOptions(settings.CallbackPort), allowLoopbackHttp: settings.Issuer.Scheme == Uri.UriSchemeHttp);

    [Fact(Timeout = 120_000)]
    public async Task BrowserLoginCachesTokenReportsStatusAndLogsOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = ResolveSettings();
        await using var browser = await ProviderBrowser.CreateAsync(host, LoginPage, settings, ct);
        var auth = new CliAuthenticator([CreateProfile(settings, settings.ClientId)], new MemoryTokenStore(), browser: browser);
        var login = await LoginAsync(auth, new LoginOptions(), browser, ct);
        Assert.False(string.IsNullOrWhiteSpace(login.Subject));
        Assert.Equal(settings.Issuer.OriginalString, login.Issuer.OriginalString);
        Assert.Contains("openid", login.GrantedScopes);
        var access = await auth.GetAccessTokenAsync(ProfileName, ct);
        Assert.False(string.IsNullOrWhiteSpace(access.Value));
        Assert.Equal(login.ExpiresAtUtc, access.ExpiresAtUtc);
        var status = await auth.GetStatusAsync(ProfileName, ct);
        Assert.Equal(SessionStatusKind.Available, status.Kind);
        Assert.Equal(login.Subject, status.Subject);
        Assert.True((await auth.LogoutAsync(ProfileName, new LogoutOptions(), ct)).LocalSessionRemoved);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync(ProfileName, ct)).Kind);
        Assert.Equal(AuthErrorCode.ReauthenticationRequired,
            (await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync(ProfileName, ct))).ErrorCode);
        Assert.Equal(1, browser.OpenCount);
    }

    [Fact(Timeout = 180_000)]
    public async Task DeviceLoginCompletesWithoutBrowserLaunchAndCachesToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = ResolveSettings();
        var auth = new CliAuthenticator([CreateProfile(settings, settings.DeviceClientId)], new MemoryTokenStore(),
            browser: new ForbiddenBrowser());
        var presented = 0;
        var login = await LoginAsync(auth, new LoginOptions(LoginFlow.Device, async (prompt, token) =>
        {
            presented++;
            Assert.False(string.IsNullOrWhiteSpace(prompt.UserCode));
            await ProviderBrowser.VerifyDeviceAsync(host, LoginPage, settings, prompt, approve: true, output, token);
        }), null, ct);
        Assert.Equal(1, presented);
        Assert.False(string.IsNullOrWhiteSpace(login.Subject));
        Assert.Contains("openid", login.GrantedScopes);
        Assert.Equal(login.ExpiresAtUtc, (await auth.GetAccessTokenAsync(ProfileName, ct)).ExpiresAtUtc);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync(ProfileName, ct)).Kind);
        Assert.True((await auth.LogoutAsync(ProfileName, new LogoutOptions(), ct)).LocalSessionRemoved);
    }

    // Instead of waiting for expiry, the authenticator's clock is moved into the hard margin (as the Keycloak suite does).
    [Fact(Timeout = 120_000)]
    public async Task ExpiringTokenIsRefreshedSilently()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = ResolveSettings();
        await using var browser = await ProviderBrowser.CreateAsync(host, LoginPage, settings, ct);
        var clock = new ShiftedClock();
        var store = new MemoryTokenStore();
        var profile = CreateProfile(settings, settings.ClientId);
        var key = SessionKey.ForProfile(profile);
        var auth = new CliAuthenticator([profile], store, browser: browser, timeProvider: clock);
        var login = await LoginAsync(auth, new LoginOptions(), browser, ct);
        var loggedIn = (await store.ReadAsync(key, ct))!;
        Assert.True(loggedIn.RefreshToken is not null,
            $"{Definition.Name} issued no refresh token. Check the requested scopes (for example offline_access) and the client's grants.");

        clock.Offset = login.ExpiresAtUtc - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        Assert.Equal(SessionStatusKind.RefreshRequired, (await auth.GetStatusAsync(ProfileName, ct)).Kind);
        var refreshed = await auth.GetAccessTokenAsync(ProfileName, ct);
        Assert.NotEqual(loggedIn.AccessToken!.Value, refreshed.Value);
        var afterRefresh = (await store.ReadAsync(key, ct))!;
        Assert.Equal(loggedIn.Generation + 1, afterRefresh.Generation);
        Assert.Equal(loggedIn.Epoch, afterRefresh.Epoch);
        Assert.Equal(login.Subject, afterRefresh.Subject);
        Assert.NotNull(afterRefresh.RefreshToken);
        if (Definition.Rotation == RefreshRotation.None)
        {
            Assert.Equal(loggedIn.RefreshToken, afterRefresh.RefreshToken);
        }
        else
        {
            Assert.NotEqual(loggedIn.RefreshToken, afterRefresh.RefreshToken);
        }
        Assert.Equal(1, browser.OpenCount);

        if (Definition.Rotation == RefreshRotation.Strict)
        {
            // Put the used refresh token back: a strictly rotating provider answers invalid_grant.
            await store.WriteAsync(key, new StoredSession(afterRefresh.Generation + 1, afterRefresh.Epoch,
                SessionLifecycleState.Active, loggedIn.AccessToken, loggedIn.RefreshToken, afterRefresh.Issuer,
                afterRefresh.ClientId, afterRefresh.Subject), ct);
            var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync(ProfileName, ct));
            Assert.Equal(AuthErrorCode.ReauthenticationRequired, error.ErrorCode);
            Assert.Equal("invalid_grant", error.ProviderErrorCode);
            Assert.Null((await store.ReadAsync(key, ct))!.RefreshToken);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task DeniedConsentSavesNoSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = ResolveSettings();
        if (!Definition.CanDenyConsent)
        {
            Assert.Skip($"{Definition.Name} declares no consent-denial path for this test client.");
        }
        await using var browser = await ProviderBrowser.CreateAsync(host, LoginPage, settings, ct);
        browser.Interaction = BrowserInteraction.DenyConsent;
        var auth = new CliAuthenticator([CreateProfile(settings, settings.ConsentClientId!)], new MemoryTokenStore(), browser: browser);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => LoginAsync(auth, new LoginOptions(), browser, ct));
        Assert.Equal(AuthErrorCode.AccessDenied, error.ErrorCode);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync(ProfileName, ct)).Kind);
    }

    [Fact(Timeout = 120_000)]
    public async Task SecondLoginReusesProviderSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = ResolveSettings();
        if (!Definition.ReusesProviderSession)
        {
            Assert.Skip($"{Definition.Name} declares that a second login does not reuse the provider session.");
        }
        await using var browser = await ProviderBrowser.CreateAsync(host, LoginPage, settings, ct);
        var auth = new CliAuthenticator([CreateProfile(settings, settings.ClientId)], new MemoryTokenStore(), browser: browser);
        var first = await LoginAsync(auth, new LoginOptions(), browser, ct);
        await auth.LogoutAsync(ProfileName, new LogoutOptions(), ct);
        browser.Interaction = BrowserInteraction.ReuseSession;
        var second = await LoginAsync(auth, new LoginOptions(), browser, ct);
        Assert.Equal(first.Subject, second.Subject);
        Assert.Equal(2, browser.OpenCount);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync(ProfileName, ct)).Kind);
    }

    private async Task<LoginResult> LoginAsync(CliAuthenticator auth, LoginOptions options, ProviderBrowser? browser, CancellationToken ct)
    {
        try { return await auth.LoginAsync(ProfileName, options, ct); }
        catch (CliAuthException error)
        {
            // Error codes and the provider's OAuth error code only; never messages, URLs or identifiers.
            output.WriteLine($"{Definition.Name} login failed: {error.ErrorCode}; provider code: {error.ProviderErrorCode ?? "none"}; " +
                $"browser stage: {browser?.Stage ?? "n/a"}");
            throw;
        }
        catch
        {
            output.WriteLine($"{Definition.Name} login failed; browser stage: {browser?.Stage ?? "n/a"}");
            throw;
        }
    }

    private sealed class ForbiddenBrowser : ISystemBrowser
    {
        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Device login attempted to launch a browser.");
    }

    private sealed class ShiftedClock : TimeProvider
    {
        internal TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
