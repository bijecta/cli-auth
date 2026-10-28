using System.Net;
using System.Net.Sockets;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.KeycloakTests;

[Collection(KeycloakCollection.Name)]
public sealed class BrowserIntegrationTests(KeycloakFixture fixture, ITestOutputHelper output)
{
    [Fact(Timeout = 90_000)]
    public async Task LoginAcquiresCachedTokenReportsStatusAndRemovesLocalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture);
        var auth = Create(browser);
        var login = await LoginAsync(auth, browser, ct);
        Assert.Equal(fixture.Issuer.OriginalString, login.Issuer.OriginalString);
        Assert.Equal(PersistenceMode.MemoryOnly, login.Persistence);
        Assert.False(string.IsNullOrWhiteSpace(login.Subject));
        Assert.Equal(["openid"], login.GrantedScopes);
        var access = await auth.GetAccessTokenAsync("work", ct);
        Assert.False(string.IsNullOrWhiteSpace(access.Value));
        Assert.Equal(login.ExpiresAtUtc, access.ExpiresAtUtc);
        var status = await auth.GetStatusAsync("work", ct);
        Assert.Equal(SessionStatusKind.Available, status.Kind);
        Assert.Equal(login.Subject, status.Subject);
        Assert.Equal(1, browser.OpenCount);
        var logout = await auth.LogoutAsync("work", new LogoutOptions(), ct);
        Assert.True(logout.LocalSessionRemoved);
        Assert.Equal(RevocationOutcome.NotRequested, logout.RevocationOutcome);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, (await Assert.ThrowsAsync<CliAuthException>(
            () => auth.GetAccessTokenAsync("work", ct))).ErrorCode);
        AssertCallbackPortReleased();
    }

    [Fact(Timeout = 90_000)]
    public async Task LocalLogoutAllowsSecondLoginUsingExistingProviderBrowserSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture);
        var auth = Create(browser);
        var first = await LoginAsync(auth, browser, ct);
        await auth.LogoutAsync("work", new LogoutOptions(), ct);
        browser.Interaction = BrowserInteraction.Sso;
        var second = await LoginAsync(auth, browser, ct);
        Assert.Equal(first.Subject, second.Subject);
        Assert.Equal(2, browser.OpenCount);
        Assert.Equal(1, browser.CredentialSubmissions);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    [Fact(Timeout = 90_000)]
    public async Task AcceptedConsentReturnsOnlyRequestedScopes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture, BrowserInteraction.AcceptConsent);
        var auth = Create(browser, KeycloakFixture.AcceptClientId);
        var login = await LoginAsync(auth, browser, ct);
        Assert.Equal(["openid", "profile"], login.GrantedScopes);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    [Fact(Timeout = 90_000)]
    public async Task DeniedConsentReturnsAccessDeniedAndStoresNoSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture, BrowserInteraction.DenyConsent);
        var auth = Create(browser, KeycloakFixture.DenyClientId);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => LoginAsync(auth, browser, ct));
        Assert.Equal(AuthErrorCode.AccessDenied, error.ErrorCode);
        Assert.Equal("access_denied", error.ProviderErrorCode);
        Assert.Null(error.InnerException);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        AssertCallbackPortReleased();
    }

    [Fact(Timeout = 90_000)]
    public async Task CancellationAtRealProviderLoginReleasesListenerWithoutSavingSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture, BrowserInteraction.WaitForCancellation);
        var auth = Create(browser);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var login = auth.LoginAsync("work", new LoginOptions(), cancel.Token);
        try
        {
            // Surface an early login failure instead of waiting for a readiness signal that cannot arrive.
            await Task.WhenAny(login, browser.LoginPageReady).WaitAsync(ct);
            if (login.IsCompleted)
            {
                await login;
                throw new InvalidOperationException("Login unexpectedly completed before cancellation at the provider form.");
            }
            await browser.LoginPageReady.WaitAsync(ct);
        }
        finally { await cancel.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        AssertCallbackPortReleased();
    }

    [Fact(Timeout = 90_000)]
    public async Task OccupiedCallbackPortFailsBeforeCallingBrowserLauncher()
    {
        var ct = TestContext.Current.CancellationToken;
        using var occupied = new TcpListener(IPAddress.Loopback, fixture.CallbackPort);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        await using var browser = await PlaywrightSystemBrowser.CreateAsync(fixture);
        var auth = Create(browser);
        var error = await Assert.ThrowsAsync<CliAuthException>(() => auth.LoginAsync("work", new LoginOptions(), ct));
        Assert.Equal(AuthErrorCode.CallbackRejected, error.ErrorCode);
        Assert.Equal(0, browser.OpenCount);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    private CliAuthenticator Create(PlaywrightSystemBrowser browser, string clientId = KeycloakFixture.ClientId) =>
        new([fixture.Profile(clientId)], new MemoryTokenStore(), browser: browser);

    private async Task<LoginResult> LoginAsync(ICliAuthenticator auth, PlaywrightSystemBrowser browser, CancellationToken ct)
    {
        try { return await auth.LoginAsync("work", new LoginOptions(), ct); }
        catch
        {
            output.WriteLine($"Browser stage: {browser.Stage}");
            throw;
        }
    }

    private void AssertCallbackPortReleased()
    {
        using var listener = new TcpListener(IPAddress.Loopback, fixture.CallbackPort);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        Assert.True(listener.Server.IsBound);
    }
}
