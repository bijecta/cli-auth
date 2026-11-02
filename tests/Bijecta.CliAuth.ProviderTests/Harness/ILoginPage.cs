using Bijecta.CliAuth.Core;
using Microsoft.Playwright;

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>What the automated user does on the provider's authorization page.</summary>
public enum BrowserInteraction
{
    /// <summary>Sign in; accept consent if the provider asks.</summary>
    Login,

    /// <summary>The provider session already exists: continue without entering credentials.</summary>
    ReuseSession,

    /// <summary>Sign in, then deny consent.</summary>
    DenyConsent,
}

/// <summary>Tracks the step in progress so failures can say where they stopped without printing page content.</summary>
public sealed class BrowserStage
{
    public string Current { get; set; } = "not started";
}

/// <summary>
/// One provider's page steps, kept in the test project and never in Core. Implementations interact only with the
/// provider's pages; the harness navigates to Core's URLs and waits for Core's loopback callback page.
/// </summary>
public interface ILoginPage
{
    /// <summary>Completes the authorization page until the provider redirects to the loopback callback.</summary>
    Task CompleteAuthorizationAsync(IPage page, BrowserInteraction interaction, ProviderSettings settings,
        BrowserStage stage, CancellationToken cancellationToken);

    /// <summary>
    /// Starting at the verification URI, enters the user code, signs in, and approves or denies. Returns once the
    /// provider has recorded the answer; Core's polling picks it up.
    /// </summary>
    Task CompleteDeviceVerificationAsync(IPage page, DeviceAuthorizationPrompt prompt, bool approve,
        ProviderSettings settings, BrowserStage stage, CancellationToken cancellationToken);
}
