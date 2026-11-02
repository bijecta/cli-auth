using Bijecta.CliAuth.ProviderTests.Harness;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(BrowserHost))]

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>
/// One headless Chromium for the whole run, started on first use. Skipped providers never start it, so a machine
/// without the Playwright browser can still run the harness tests.
/// </summary>
public sealed class BrowserHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    internal async Task<IBrowserContext> NewContextAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is null)
            {
                try
                {
                    _playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken);
                    _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 30_000 }).WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"Could not start headless Chromium ({error.GetType().Name}). " +
                        "Install it with playwright.ps1 install chromium; see the provider tests README.");
                }
            }
            return await _browser.NewContextAsync(new() { Locale = "en-US" });
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        _playwright?.Dispose();
        _gate.Dispose();
    }
}
