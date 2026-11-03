using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.ProviderTests.Harness;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Auth0;

// A hosted Auth0 tenant configured as in docs/providers/auth0.md: two first-party native applications (browser and
// device) on a database connection, rotation with no overlap period, and a fixed registered callback port.
public sealed class Auth0Scenarios(BrowserHost host, ITestOutputHelper output) : ProviderScenarios(host, output)
{
    internal static readonly ProviderDefinition Provider = new("Auth0", "AUTH0",
        // First-party applications are not asked for consent, so there is no denial path to test.
        RefreshRotation.Strict, CanDenyConsent: false, ReusesProviderSession: true, DefaultScopes: ["profile", "offline_access"]);

    protected override ProviderDefinition Definition => Provider;
    protected override ILoginPage LoginPage { get; } = new Auth0LoginPage();
}

// Auth0 Universal Login with the "Identifier + Password" authentication profile.
internal sealed class Auth0LoginPage : ILoginPage
{
    private const string Submit = "button[type=submit][name=action][value=default]";

    public async Task CompleteAuthorizationAsync(IPage page, BrowserInteraction interaction, ProviderSettings settings,
        BrowserStage stage, CancellationToken cancellationToken)
    {
        if (interaction != BrowserInteraction.ReuseSession)
        {
            await SignInAsync(page, settings, stage, cancellationToken);
        }
        // Auth0 may ask for consent when the callback is on loopback, which it cannot verify.
        stage.Current = "optional consent";
        var callback = page.GetByText(ProviderBrowser.CallbackText, new() { Exact = true });
        var accept = page.Locator("button[name=action][value=accept]");
        await callback.Or(accept).WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
        if (await accept.IsVisibleAsync())
        {
            stage.Current = "consent form";
            await accept.ClickAsync().WaitAsync(cancellationToken);
        }
    }

    public async Task CompleteDeviceVerificationAsync(IPage page, DeviceAuthorizationPrompt prompt, bool approve,
        ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "user code entry";
        await page.Locator("input[name=code]").FillAsync(prompt.UserCode).WaitAsync(cancellationToken);
        await page.Locator(Submit).ClickAsync().WaitAsync(cancellationToken);
        // Auth0 asks the user to confirm the code before signing in.
        stage.Current = "device confirmation";
        var confirm = page.Locator("button[name=action][value=confirm]");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
        if (!approve)
        {
            await page.Locator("button[name=action][value=cancel]").ClickAsync().WaitAsync(cancellationToken);
            stage.Current = "device answer recorded";
            await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached }).WaitAsync(cancellationToken);
            return;
        }
        await confirm.ClickAsync().WaitAsync(cancellationToken);
        await SignInAsync(page, settings, stage, cancellationToken);
        stage.Current = "device answer recorded";
        await page.Locator("input#password").WaitForAsync(new() { State = WaitForSelectorState.Detached }).WaitAsync(cancellationToken);
    }

    private static async Task SignInAsync(IPage page, ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "username entry";
        await page.Locator("input#username").FillAsync(settings.UserName).WaitAsync(cancellationToken);
        stage.Current = "password entry";
        await page.Locator("input#password").FillAsync(settings.Password).WaitAsync(cancellationToken);
        stage.Current = "credential submission";
        await page.Locator(Submit).ClickAsync().WaitAsync(cancellationToken);
    }
}
