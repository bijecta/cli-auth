using Bijecta.CliAuth.Core;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.KeycloakTests;

internal enum BrowserInteraction { Login, Sso, AcceptConsent, DenyConsent, WaitForCancellation }

// Only this host extension is replaced. Core still uses real discovery, HTTP, callbacks, and validation.
internal sealed class PlaywrightSystemBrowser : ISystemBrowser, IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly KeycloakFixture _fixture;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal BrowserInteraction Interaction { get; set; }
    internal int OpenCount { get; private set; }
    internal int CredentialSubmissions { get; private set; }
    internal Task LoginPageReady => _ready.Task;
    internal string Stage { get; private set; } = "not opened";

    private PlaywrightSystemBrowser(IBrowserContext context, IPage page, KeycloakFixture fixture, BrowserInteraction interaction)
    {
        _context = context;
        _page = page;
        _fixture = fixture;
        Interaction = interaction;
        page.SetDefaultTimeout(15_000);
        page.SetDefaultNavigationTimeout(30_000);
    }

    internal static async Task<PlaywrightSystemBrowser> CreateAsync(KeycloakFixture fixture,
        BrowserInteraction interaction = BrowserInteraction.Login)
    {
        var context = await fixture.Browser.NewContextAsync(new() { Locale = "en-US" });
        try { return new(context, await context.NewPageAsync(), fixture, interaction); }
        catch
        {
            await context.CloseAsync();
            throw new InvalidOperationException("Could not create an isolated Playwright browser context.");
        }
    }

    public async Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        OpenCount++;
        try
        {
            Stage = "provider navigation";
            await _page.GotoAsync(authorizationUri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded })
                .WaitAsync(cancellationToken);
            if (Interaction == BrowserInteraction.WaitForCancellation)
            {
                Stage = "waiting at login form";
                await _page.Locator("#username").WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
                _ready.TrySetResult();
                return;
            }
            if (Interaction != BrowserInteraction.Sso)
            {
                Stage = "username entry";
                await _page.Locator("#username").FillAsync(KeycloakFixture.UserName).WaitAsync(cancellationToken);
                Stage = "password entry";
                await _page.Locator("#password").FillAsync(_fixture.Password).WaitAsync(cancellationToken);
                Stage = "credential submission";
                await _page.Locator("#kc-login").ClickAsync().WaitAsync(cancellationToken);
                CredentialSubmissions++;
            }
            if (Interaction is BrowserInteraction.AcceptConsent or BrowserInteraction.DenyConsent)
            {
                Stage = "consent form";
                await _page.Locator("#kc-cancel").WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
                var button = Interaction == BrowserInteraction.DenyConsent ? "#kc-cancel" : "#kc-login";
                await _page.Locator(button).ClickAsync().WaitAsync(cancellationToken);
            }
            Stage = "loopback callback";
            await _page.GetByText("Login response received. You may close this window.", new() { Exact = true })
                .WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
            Stage = "callback received";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Playwright's raw call logs can contain the full authorization/callback URL or input values.
            throw new InvalidOperationException($"Browser automation failed during {Stage}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { throw new InvalidOperationException("Could not close the integration browser context."); }
    }
}
