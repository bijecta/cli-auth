using System.Text.Json;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.ProviderTests.Harness;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Duende;

// Self-hosted and secret-free, so it always runs, including on pull requests in the required integration job.
public sealed class DuendeScenarios(BrowserHost host, DuendeFixture fixture, ITestOutputHelper output)
    : ProviderScenarios(host, output), IClassFixture<DuendeFixture>
{
    internal static readonly ProviderDefinition Provider = new("Duende IdentityServer", "DUENDE",
        RefreshRotation.Strict, CanDenyConsent: true, ReusesProviderSession: true, DefaultScopes: DuendeFixture.Scopes);

    protected override ProviderDefinition Definition => Provider;
    protected override ILoginPage LoginPage { get; } = new DuendeLoginPage();
    protected override ProviderSettings ResolveSettings() => fixture.Settings;

    // Evidence for the compatibility table's discovery columns.
    [Fact(Timeout = 30_000)]
    public async Task DiscoveryAdvertisesDeviceRevocationAndS256()
    {
        using var http = new HttpClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync(
            new Uri(fixture.Issuer, "/.well-known/openid-configuration"), TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal(fixture.Issuer.GetLeftPart(UriPartial.Authority), root.GetProperty("issuer").GetString());
        Assert.True(root.TryGetProperty("device_authorization_endpoint", out _));
        Assert.True(root.TryGetProperty("revocation_endpoint", out _));
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(value => value.GetString()));
        Output.WriteLine($"acr_values_supported advertised: {root.TryGetProperty("acr_values_supported", out _)}");
    }
}

internal sealed class DuendeLoginPage : ILoginPage
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
            await page.Locator("#deny").ClickAsync().WaitAsync(cancellationToken);
        }
    }

    public async Task CompleteDeviceVerificationAsync(IPage page, DeviceAuthorizationPrompt prompt, bool approve,
        ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "user code entry";
        await page.Locator("#user-code").FillAsync(prompt.UserCode).WaitAsync(cancellationToken);
        await page.Locator("#submit-code").ClickAsync().WaitAsync(cancellationToken);
        await SignInAsync(page, settings, stage, cancellationToken);
        stage.Current = "device consent";
        await page.Locator(approve ? "#allow" : "#deny").ClickAsync().WaitAsync(cancellationToken);
        stage.Current = "device answer recorded";
        await page.Locator("#done").WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);
    }

    private static async Task SignInAsync(IPage page, ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "username entry";
        await page.Locator("#username").FillAsync(settings.UserName).WaitAsync(cancellationToken);
        stage.Current = "password entry";
        await page.Locator("#password").FillAsync(settings.Password).WaitAsync(cancellationToken);
        stage.Current = "credential submission";
        await page.Locator("#login").ClickAsync().WaitAsync(cancellationToken);
    }
}
