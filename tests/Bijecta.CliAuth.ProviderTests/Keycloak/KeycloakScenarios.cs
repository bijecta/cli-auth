using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.ProviderTests.Harness;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Keycloak;

// The harness's reference provider, pointed at the developer fixture in samples/keycloak (not a hosted provider).
// The disposable, CI-required Keycloak coverage stays in Bijecta.CliAuth.KeycloakTests.
public sealed class KeycloakScenarios(BrowserHost host, ITestOutputHelper output) : ProviderScenarios(host, output)
{
    internal static readonly ProviderDefinition Provider = new("Keycloak", "KEYCLOAK",
        // The fixture realm keeps Keycloak's default: a new refresh token per refresh, the old one still accepted.
        RefreshRotation.Rotates, CanDenyConsent: false, ReusesProviderSession: true, DefaultScopes: ["profile"]);

    protected override ProviderDefinition Definition => Provider;
    protected override ILoginPage LoginPage { get; } = new KeycloakLoginPage();
}

internal sealed class KeycloakLoginPage : ILoginPage
{
    public async Task CompleteAuthorizationAsync(IPage page, BrowserInteraction interaction, ProviderSettings settings,
        BrowserStage stage, CancellationToken cancellationToken)
    {
        if (interaction == BrowserInteraction.ReuseSession)
        {
            return;
        }
        await SignInAsync(page, settings, stage, cancellationToken);
        if (interaction == BrowserInteraction.DenyConsent)
        {
            stage.Current = "consent form";
            await page.Locator("#kc-cancel").ClickAsync().WaitAsync(cancellationToken);
        }
    }

    public async Task CompleteDeviceVerificationAsync(IPage page, DeviceAuthorizationPrompt prompt, bool approve,
        ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "user code entry";
        await page.Locator("[name=device_user_code]").FillAsync(prompt.UserCode).WaitAsync(cancellationToken);
        await page.Locator("#kc-user-verify-device-user-code-form [type=submit]").ClickAsync().WaitAsync(cancellationToken);
        await SignInAsync(page, settings, stage, cancellationToken);
        // Keycloak always asks the user to grant a device its access, whatever the client's consent setting.
        stage.Current = "device consent";
        var deny = page.Locator("#kc-cancel");
        await deny.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
        await (approve ? page.Locator("#kc-login") : deny).ClickAsync().WaitAsync(cancellationToken);
        stage.Current = "device answer recorded";
        await deny.WaitForAsync(new() { State = WaitForSelectorState.Detached }).WaitAsync(cancellationToken);
    }

    private static async Task SignInAsync(IPage page, ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "username entry";
        await page.Locator("#username").FillAsync(settings.UserName).WaitAsync(cancellationToken);
        stage.Current = "password entry";
        await page.Locator("#password").FillAsync(settings.Password).WaitAsync(cancellationToken);
        stage.Current = "credential submission";
        await page.Locator("#kc-login").ClickAsync().WaitAsync(cancellationToken);
    }
}
