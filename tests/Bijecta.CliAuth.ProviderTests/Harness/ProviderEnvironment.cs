using System.Globalization;

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>Outcome of reading one provider's variables.</summary>
public enum ProviderAvailability
{
    /// <summary>Every required variable is set and valid.</summary>
    Ready,

    /// <summary>None of the provider's variables are set and the provider is not required: the tests skip.</summary>
    NotConfigured,

    /// <summary>Some variables are missing or invalid, or a required provider has none: the tests fail.</summary>
    Misconfigured,
}

/// <summary>The result of <see cref="ProviderEnvironment.Read"/>. Reasons name variables, never their values.</summary>
public sealed record ProviderResolution(ProviderAvailability Availability, string Reason, ProviderSettings? Settings);

/// <summary>
/// The skip policy (ADR 0010). A provider whose variables are all absent is skipped with a reason that names them.
/// A partial or invalid configuration fails, as does an absent configuration for a provider listed in
/// CLIAUTH_REQUIRED_PROVIDERS, which the hosted CI job sets so that a missing secret cannot look like a skip.
/// </summary>
public static class ProviderEnvironment
{
    public static ProviderResolution Read(ProviderDefinition provider, Func<string, string?> environment)
    {
        var required = IsRequired(provider, environment);
        var all = provider.RequiredVariables()
            .Concat(new[] { "DEVICE_CLIENT_ID", "CALLBACK_PORT", "SCOPES" }.Select(provider.Variable)).ToArray();
        string? Get(string name) => environment(name)?.Trim() is { Length: > 0 } value ? value : null;

        if (all.All(name => Get(name) is null))
        {
            var names = string.Join(", ", provider.RequiredVariables());
            return required
                ? new(ProviderAvailability.Misconfigured,
                    $"{provider.Name} is listed in {ProviderDefinition.RequiredProvidersVariable} but none of its variables are set ({names}).", null)
                : new(ProviderAvailability.NotConfigured, $"{provider.Name} is not configured. Set {names} to run these tests.", null);
        }

        var missing = provider.RequiredVariables().Where(name => Get(name) is null).ToArray();
        if (missing.Length > 0)
        {
            return Misconfigured(provider, $"is partly configured; missing {string.Join(", ", missing)}.");
        }

        var issuerName = provider.Variable("ISSUER");
        if (!Uri.TryCreate(Get(issuerName), UriKind.Absolute, out var issuer) || !ProviderSettings.IsAllowedIssuer(issuer)
            || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment))
        {
            return Misconfigured(provider, $"has an invalid {issuerName}: use an absolute https URI without query or fragment.");
        }

        var port = 0;
        var portName = provider.Variable("CALLBACK_PORT");
        if (Get(portName) is { } portText &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            return Misconfigured(provider, $"has an invalid {portName}: use a port number from 1 to 65535.");
        }

        var scopes = Get(provider.Variable("SCOPES")) is { } scopeText
            ? scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : provider.DefaultScopes.ToArray();
        var extras = (provider.RequiredExtras ?? []).ToDictionary(name => name, name => Get(provider.Variable(name))!, StringComparer.Ordinal);
        var settings = new ProviderSettings(provider, issuer, Get(provider.Variable("CLIENT_ID"))!,
            Get(provider.Variable("DEVICE_CLIENT_ID")), Get(provider.Variable(ProviderDefinition.ConsentClientId)),
            Get(provider.Variable("USERNAME"))!, Get(provider.Variable("PASSWORD"))!, port, scopes, extras);
        return new(ProviderAvailability.Ready, $"{provider.Name} is configured.", settings);
    }

    /// <summary>Names in CLIAUTH_REQUIRED_PROVIDERS that match none of <paramref name="known"/>.</summary>
    public static IReadOnlyList<string> UnknownRequiredProviders(IEnumerable<ProviderDefinition> known, Func<string, string?> environment)
    {
        var names = known.Select(provider => provider.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredNames(environment).Where(name => !names.Contains(name)).ToArray();
    }

    /// <summary>Returns the settings, skips the current test, or fails it, following the policy above.</summary>
    public static ProviderSettings Require(ProviderDefinition provider)
    {
        var resolution = Read(provider, Environment.GetEnvironmentVariable);
        if (resolution.Availability == ProviderAvailability.NotConfigured)
        {
            Assert.Skip(resolution.Reason);
        }
        return resolution.Settings ?? throw new InvalidOperationException(resolution.Reason);
    }

    private static bool IsRequired(ProviderDefinition provider, Func<string, string?> environment) =>
        RequiredNames(environment).Contains(provider.Name, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> RequiredNames(Func<string, string?> environment) =>
        (environment(ProviderDefinition.RequiredProvidersVariable) ?? "")
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ProviderResolution Misconfigured(ProviderDefinition provider, string detail) =>
        new(ProviderAvailability.Misconfigured, $"{provider.Name} {detail}", null);
}
