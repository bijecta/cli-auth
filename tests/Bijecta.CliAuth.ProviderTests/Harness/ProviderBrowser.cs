using Bijecta.CliAuth.Core;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Harness;

// Only the browser-launch host extension is replaced. Core still does real discovery, HTTP, callbacks and validation.
internal sealed class ProviderBrowser : ISystemBrowser, IAsyncDisposable
{
    internal const string CallbackText = "Login response received. You may close this window.";
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly ILoginPage _loginPage;
    private readonly ProviderSettings _settings;
    private readonly BrowserStage _stage = new();

    private ProviderBrowser(IBrowserContext context, IPage page, ILoginPage loginPage, ProviderSettings settings)
    {
        _context = context;
        _page = page;
        _loginPage = loginPage;
        _settings = settings;
        Configure(page);
    }

    internal BrowserInteraction Interaction { get; set; } = BrowserInteraction.Login;
    internal int OpenCount { get; private set; }
    internal string Stage => _stage.Current;

    internal static async Task<ProviderBrowser> CreateAsync(BrowserHost host, ILoginPage loginPage, ProviderSettings settings,
        CancellationToken cancellationToken)
    {
        var context = await host.NewContextAsync(cancellationToken);
        try { return new(context, await context.NewPageAsync(), loginPage, settings); }
        catch
        {
            await context.CloseAsync();
            throw new InvalidOperationException("Could not create an isolated browser context.");
        }
    }

    public async Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        OpenCount++;
        var provider = _settings.Provider.Name;
        try
        {
            _stage.Current = "provider navigation";
            await _page.GotoAsync(authorizationUri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded })
                .WaitAsync(cancellationToken);
            await _loginPage.CompleteAuthorizationAsync(_page, Interaction, _settings, _stage, cancellationToken);
            _stage.Current = "loopback callback";
            await _page.GetByText(CallbackText, new() { Exact = true })
                .WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
            _stage.Current = "callback received";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Playwright's call logs can contain authorization or callback URLs and typed values.
            throw new InvalidOperationException($"{provider} browser automation failed during {_stage.Current}.");
        }
    }

    // The host's device presentation callback acting as the user, in its own context. Core never receives it.
    internal static async Task VerifyDeviceAsync(BrowserHost host, ILoginPage loginPage, ProviderSettings settings,
        DeviceAuthorizationPrompt prompt, bool approve, ITestOutputHelper output, CancellationToken cancellationToken)
    {
        var stage = new BrowserStage();
        var context = await host.NewContextAsync(cancellationToken);
        try
        {
            var page = await context.NewPageAsync();
            Configure(page);
            stage.Current = "verification page";
            await page.GotoAsync(prompt.VerificationUri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded })
                .WaitAsync(cancellationToken);
            await loginPage.CompleteDeviceVerificationAsync(page, prompt, approve, settings, stage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            output.WriteLine($"Device browser stage: {stage.Current}");
            throw new InvalidOperationException($"{settings.Provider.Name} device verification failed during {stage.Current}.");
        }
        finally
        {
            try { await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { throw new InvalidOperationException("Could not close the provider browser context."); }
    }

    private static void Configure(IPage page)
    {
        page.SetDefaultTimeout(20_000);
        page.SetDefaultNavigationTimeout(30_000);
    }
}
