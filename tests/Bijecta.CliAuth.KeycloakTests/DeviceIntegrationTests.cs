using System.Net;
using System.Net.Sockets;
using Bijecta.CliAuth.Core;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.KeycloakTests;

[Collection(KeycloakCollection.Name)]
public sealed class DeviceIntegrationTests(KeycloakFixture fixture, ITestOutputHelper output)
{
    [Fact(Timeout = 90_000)]
    public async Task DeviceLoginWorksWithOccupiedCallbackPortAndSupportsCachedSessionLifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var occupied = new TcpListener(IPAddress.Loopback, fixture.CallbackPort);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var auth = Create(KeycloakFixture.DeviceClientId);
        await using var interaction = await DeviceInteraction.CreateAsync(fixture, output);
        var login = await LoginAsync(auth, new LoginOptions(LoginFlow.Device, interaction.AcceptAsync), ct);
        Assert.False(string.IsNullOrWhiteSpace(login.Subject));
        Assert.Equal(fixture.Issuer, login.Issuer);
        Assert.Equal(["openid", "profile"], login.GrantedScopes);
        Assert.Equal(PersistenceMode.MemoryOnly, login.Persistence);
        var access = await auth.GetAccessTokenAsync("work", ct);
        Assert.False(string.IsNullOrWhiteSpace(access.Value));
        Assert.Equal(login.ExpiresAtUtc, access.ExpiresAtUtc);
        Assert.Equal(SessionStatusKind.Available, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.True((await auth.LogoutAsync("work", new LogoutOptions(), ct)).LocalSessionRemoved);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
        Assert.Equal(AuthErrorCode.ReauthenticationRequired, (await Assert.ThrowsAsync<CliAuthException>(() => auth.GetAccessTokenAsync("work", ct))).ErrorCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task DeviceConsentDenialDoesNotCreateSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = Create(KeycloakFixture.DeviceDenyClientId);
        await using var interaction = await DeviceInteraction.CreateAsync(fixture, output);
        var error = await Assert.ThrowsAsync<CliAuthException>(() =>
            LoginAsync(auth, new LoginOptions(LoginFlow.Device, interaction.DenyAsync), ct));
        Assert.Equal(AuthErrorCode.AccessDenied, error.ErrorCode);
        Assert.Equal("access_denied", error.ProviderErrorCode);
        Assert.Null(error.InnerException);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    [Fact(Timeout = 90_000)]
    public async Task CancellationAfterRealDeviceAuthorizationDoesNotCreateSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var auth = Create(KeycloakFixture.DeviceClientId);
        var presented = false;
        var login = LoginAsync(auth, new LoginOptions(LoginFlow.Device, (prompt, token) =>
        {
            presented = true;
            Assert.False(string.IsNullOrWhiteSpace(prompt.UserCode));
            cancel.Cancel();
            return Task.CompletedTask;
        }), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.True(presented);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    [Fact(Timeout = 90_000)]
    public async Task DisabledDeviceGrantFailsWithoutPresentingOrFallingBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = Create(KeycloakFixture.NoDeviceClientId);
        var presented = false;
        var error = await Assert.ThrowsAsync<CliAuthException>(() => LoginAsync(auth,
            new LoginOptions(LoginFlow.Device, (_, _) => { presented = true; return Task.CompletedTask; }), ct));
        // Discovery describes server capabilities; a disabled client grant is rejected by the endpoint.
        Assert.Equal(AuthErrorCode.ProtocolFailure, error.ErrorCode);
        Assert.False(presented);
        Assert.Equal(SessionStatusKind.None, (await auth.GetStatusAsync("work", ct)).Kind);
    }

    private async Task<LoginResult> LoginAsync(ICliAuthenticator auth, LoginOptions options, CancellationToken ct)
    {
        try { return await auth.LoginAsync("work", options, ct); }
        catch (CliAuthException error)
        {
            output.WriteLine($"Device failure: {error.ErrorCode}; provider code: {error.ProviderErrorCode ?? "none"}");
            throw;
        }
    }

    private CliAuthenticator Create(string clientId) =>
        new([fixture.Profile(clientId)], new MemoryTokenStore(), browser: new ForbiddenBrowser());

    private sealed class ForbiddenBrowser : ISystemBrowser
    {
        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Device login attempted to launch a browser.");
    }

    // This is the host presentation callback acting as the user. Core never receives this browser.
    private sealed class DeviceInteraction(IBrowserContext context, IPage page, KeycloakFixture fixture, ITestOutputHelper output) : IAsyncDisposable
    {
        internal static async Task<DeviceInteraction> CreateAsync(KeycloakFixture fixture, ITestOutputHelper output)
        {
            var context = await fixture.Browser.NewContextAsync(new() { Locale = "en-US" });
            try
            {
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(15_000);
                page.SetDefaultNavigationTimeout(30_000);
                return new(context, page, fixture, output);
            }
            catch
            {
                await context.CloseAsync();
                throw new InvalidOperationException("Could not create device verification browser context.");
            }
        }
        internal Task AcceptAsync(DeviceAuthorizationPrompt prompt, CancellationToken ct) => CompleteAsync(prompt, false, ct);
        internal Task DenyAsync(DeviceAuthorizationPrompt prompt, CancellationToken ct) => CompleteAsync(prompt, true, ct);
        private async Task CompleteAsync(DeviceAuthorizationPrompt prompt, bool deny, CancellationToken ct)
        {
            var stage = "verification page";
            try
            {
                await page.GotoAsync(prompt.VerificationUri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
                stage = "user code entry";
                await page.Locator("[name=device_user_code]").FillAsync(prompt.UserCode).WaitAsync(ct);
                await page.Locator("#kc-user-verify-device-user-code-form [type=submit]").ClickAsync().WaitAsync(ct);
                stage = "credentials";
                await page.Locator("#username").FillAsync(KeycloakFixture.UserName).WaitAsync(ct);
                await page.Locator("#password").FillAsync(fixture.Password).WaitAsync(ct);
                await page.Locator("#kc-login").ClickAsync().WaitAsync(ct);
                stage = "device consent";
                await page.Locator("#kc-cancel").WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(ct);
                await page.Locator(deny ? "#kc-cancel" : "#kc-login").ClickAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                output.WriteLine($"Device browser stage: {stage}");
                throw new InvalidOperationException($"Device verification automation failed during {stage}.");
            }

        }
        public async ValueTask DisposeAsync()
        {
            try { await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { throw new InvalidOperationException("Could not close device verification browser context."); }
        }
    }
}
