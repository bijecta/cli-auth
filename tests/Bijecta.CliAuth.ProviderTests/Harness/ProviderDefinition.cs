namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>How a provider treats a refresh token after using it.</summary>
public enum RefreshRotation
{
    /// <summary>The same refresh token stays valid; the provider may omit a new one.</summary>
    None,

    /// <summary>A new refresh token is issued, but the used one is still accepted (a reuse window or count).</summary>
    Rotates,

    /// <summary>A new refresh token is issued and reusing the old one is rejected with invalid_grant.</summary>
    Strict,
}

/// <summary>What one provider's tests need and what its scenarios may assert.</summary>
/// <param name="Name">Display name and the value used in CLIAUTH_REQUIRED_PROVIDERS (case-insensitive).</param>
/// <param name="Key">Environment variable segment: CLIAUTH_{Key}_ISSUER and so on.</param>
/// <param name="RequiredExtras">Provider-specific variables (without the CLIAUTH_{Key}_ prefix) that must be set.</param>
/// <param name="DefaultScopes">Scopes requested when CLIAUTH_{Key}_SCOPES is not set; openid is always added.</param>
/// <param name="RefreshAhead">
/// When set, the profile's early-refresh window, and the refresh scenario moves the clock into it instead of into the
/// hard expiry margin. For providers whose ID token expires before the access token (Entra: 1 h against 60-90 min), a
/// refreshed ID token would already be expired at a clock moved that far.
/// </param>
public sealed record ProviderDefinition(
    string Name,
    string Key,
    RefreshRotation Rotation,
    bool CanDenyConsent,
    bool ReusesProviderSession,
    IReadOnlyList<string> DefaultScopes,
    IReadOnlyList<string>? RequiredExtras = null,
    TimeSpan? RefreshAhead = null)
{
    internal const string Prefix = "CLIAUTH_";
    internal const string RequiredProvidersVariable = "CLIAUTH_REQUIRED_PROVIDERS";

    /// <summary>Variables every provider needs.</summary>
    internal static readonly IReadOnlyList<string> CoreVariables = ["ISSUER", "CLIENT_ID", "USERNAME", "PASSWORD"];

    /// <summary>The consent-denial scenario needs a client that always asks for consent.</summary>
    internal const string ConsentClientId = "CONSENT_CLIENT_ID";

    internal string Variable(string suffix) => $"{Prefix}{Key}_{suffix}";

    internal IEnumerable<string> RequiredVariables()
    {
        foreach (var name in CoreVariables)
        {
            yield return Variable(name);
        }
        if (CanDenyConsent)
        {
            yield return Variable(ConsentClientId);
        }
        foreach (var name in RequiredExtras ?? [])
        {
            yield return Variable(name);
        }
    }
}
