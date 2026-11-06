using System.Text.RegularExpressions;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.ProviderTests.Harness;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Entra;

// A single-tenant Microsoft Entra ID directory configured as in docs/providers/entra.md: two public-client app
// registrations (browser and device) with admin consent, and a cloud-only test user with no MFA registration.
public sealed class EntraScenarios(BrowserHost host, ITestOutputHelper output) : ProviderScenarios(host, output)
{
    internal static readonly ProviderDefinition Provider = new("Entra", "ENTRA",
        // Entra issues a new refresh token on each refresh but does not revoke the one it used.
        // Admin consent is granted, so the user is never asked and there is no denial path.
        // Its token responses list every scope the app has consent for (such as User.Read); Core ignores the unrequested
        // ones. Its 1 h ID token expires before the 60-90 min access token, so the refresh scenario uses the
        // early-refresh window.
        RefreshRotation.Rotates, CanDenyConsent: false, ReusesProviderSession: true,
        DefaultScopes: ["profile", "offline_access"], RefreshAhead: TimeSpan.FromMinutes(30));

    protected override ProviderDefinition Definition => Provider;
    protected override ILoginPage LoginPage { get; } = new EntraLoginPage();

    // The device flow's verification_uri is https://login.microsoft.com/device, outside the issuer's origin.
    internal static readonly Uri VerificationOrigin = new("https://login.microsoft.com");

    protected override OidcProfile CreateProfile(ProviderSettings settings, string clientId) =>
        new("cliauth-provider-tests", "work", settings.Issuer, clientId, settings.Scopes, PersistenceMode.MemoryOnly,
            new BrowserCallbackOptions(settings.CallbackPort), allowedEndpointOrigins: [VerificationOrigin],
            refreshAhead: Provider.RefreshAhead);
}

// The Microsoft sign-in pages at login.microsoftonline.com. Several pages are optional ("Stay signed in?", the account
// picker, the device confirmation), so the steps react to whichever page is shown until the flow's end page appears.
internal sealed partial class EntraLoginPage : ILoginPage
{
    private const string Primary = "#idSIButton9";
    private const string Secondary = "#idBtn_Back";
    // The newer device confirmation page ("Approve sign-in on another device") uses its own buttons.
    private const string Approve = "#idBtn_Approve, #idSIButton9";
    private const string Decline = "#idBtn_Cancel, #idBtn_Back";
    private const int MaxSteps = 10;

    public Task CompleteAuthorizationAsync(IPage page, BrowserInteraction interaction, ProviderSettings settings,
        BrowserStage stage, CancellationToken cancellationToken) =>
        RunAsync(page, settings, stage, page.GetByText(ProviderBrowser.CallbackText, new() { Exact = true }),
            allowCredentials: interaction != BrowserInteraction.ReuseSession, approve: true, cancellationToken);

    public async Task CompleteDeviceVerificationAsync(IPage page, DeviceAuthorizationPrompt prompt, bool approve,
        ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken)
    {
        stage.Current = "user code entry";
        var code = page.Locator("input[name=otc]");
        await code.FillAsync(prompt.UserCode).WaitAsync(cancellationToken);
        await page.Locator(Primary).ClickAsync().WaitAsync(cancellationToken);
        await code.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).WaitAsync(cancellationToken);
        // The end page says the user signed in (or declined) and may close the window.
        await RunAsync(page, settings, stage, page.GetByText(CloseWindow()), allowCredentials: true, approve, cancellationToken);
    }

    private static async Task RunAsync(IPage page, ProviderSettings settings, BrowserStage stage, ILocator done,
        bool allowCredentials, bool approve, CancellationToken cancellationToken)
    {
        // Entra keeps inactive panels (such as the password panel) in the DOM off-screen, so pages are told apart by
        // their visible heading rather than by which inputs exist.
        var signIn = Heading(page, "Sign in");
        var enterPassword = Heading(page, "Enter password");
        var staySignedIn = Heading(page, "Stay signed in?");
        var picker = Heading(page, "Pick an account");
        var consent = Heading(page, "Permissions requested");
        var deviceConfirm = page.GetByRole(AriaRole.Heading, new() { NameRegex = DeviceConfirmation() });

        for (var step = 0; step < MaxSteps; step++)
        {
            stage.Current = step == 0 ? "first page" : "next page";
            await done.Or(signIn).Or(enterPassword).Or(staySignedIn).Or(picker).Or(consent).Or(deviceConfirm).First
                .WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(cancellationToken);

            if (await done.IsVisibleAsync())
            {
                return;
            }
            if (await signIn.IsVisibleAsync())
            {
                Require(allowCredentials, stage, "username entry");
                await page.Locator("input[name=loginfmt]").FillAsync(settings.UserName).WaitAsync(cancellationToken);
                await ContinueAsync(page, signIn, Primary, cancellationToken);
            }
            else if (await enterPassword.IsVisibleAsync())
            {
                Require(allowCredentials, stage, "password entry");
                await page.Locator("input[name=passwd]").FillAsync(settings.Password).WaitAsync(cancellationToken);
                stage.Current = "credential submission";
                await ContinueAsync(page, enterPassword, Primary, cancellationToken);
            }
            else if (await picker.IsVisibleAsync())
            {
                stage.Current = "account picker";
                await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(Regex.Escape(settings.UserName), RegexOptions.IgnoreCase) })
                    .First.ClickAsync().WaitAsync(cancellationToken);
                await picker.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).WaitAsync(cancellationToken);
            }
            else if (await staySignedIn.IsVisibleAsync())
            {
                // "No" keeps the session cookie for this browser context only, which is all session reuse needs.
                stage.Current = "stay signed in";
                await ContinueAsync(page, staySignedIn, Secondary, cancellationToken);
            }
            else if (await deviceConfirm.IsVisibleAsync())
            {
                stage.Current = "device confirmation";
                await ContinueAsync(page, deviceConfirm, approve ? Approve : Decline, cancellationToken);
            }
            else if (await consent.IsVisibleAsync())
            {
                stage.Current = "consent";
                await ContinueAsync(page, consent, Primary, cancellationToken);
            }
        }
        throw new InvalidOperationException($"Entra sign-in did not finish within {MaxSteps} pages.");
    }

    private static ILocator Heading(IPage page, string name) =>
        page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true });

    private static async Task ContinueAsync(IPage page, ILocator heading, string button, CancellationToken cancellationToken)
    {
        // Inactive panels keep their buttons in the DOM, so click the visible match.
        await page.Locator(button).Locator("visible=true").First.ClickAsync().WaitAsync(cancellationToken);
        await heading.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).WaitAsync(cancellationToken);
    }

    private static void Require(bool allowCredentials, BrowserStage stage, string current)
    {
        stage.Current = current;
        if (!allowCredentials)
        {
            throw new InvalidOperationException("Entra asked for credentials although its session should be reused.");
        }
    }

    [GeneratedRegex("close this window", RegexOptions.IgnoreCase)]
    private static partial Regex CloseWindow();

    // Older and newer wording of the device confirmation page.
    [GeneratedRegex("^(Are you trying to sign in to|Approve sign-in on another device)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceConfirmation();
}
