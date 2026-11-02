using System.Net;

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>
/// One provider's test configuration, read from environment variables. Issuers, client IDs, tenant identifiers and
/// test-user credentials are all treated as secrets: <see cref="ToString"/> never prints them.
/// </summary>
public sealed class ProviderSettings
{
    internal ProviderSettings(ProviderDefinition provider, Uri issuer, string clientId, string? deviceClientId,
        string? consentClientId, string userName, string password, int callbackPort, IReadOnlyList<string> scopes,
        IReadOnlyDictionary<string, string> extras)
    {
        Provider = provider;
        Issuer = issuer;
        ClientId = clientId;
        DeviceClientId = deviceClientId ?? clientId;
        ConsentClientId = consentClientId;
        UserName = userName;
        Password = password;
        CallbackPort = callbackPort;
        Scopes = scopes;
        Extras = extras;
    }

    public ProviderDefinition Provider { get; }
    public Uri Issuer { get; }
    public string ClientId { get; }

    /// <summary>CLIAUTH_{Key}_DEVICE_CLIENT_ID, or <see cref="ClientId"/> when one client serves both flows.</summary>
    public string DeviceClientId { get; }

    public string? ConsentClientId { get; }
    public string UserName { get; }
    public string Password { get; }

    /// <summary>CLIAUTH_{Key}_CALLBACK_PORT for providers that only accept an exact registered port; 0 otherwise.</summary>
    public int CallbackPort { get; }

    public IReadOnlyList<string> Scopes { get; }

    /// <summary>The provider's <see cref="ProviderDefinition.RequiredExtras"/>, keyed by suffix.</summary>
    public IReadOnlyDictionary<string, string> Extras { get; }

    public override string ToString() => $"{Provider.Name} settings (values redacted)";

    /// <summary>Plain HTTP is accepted only for an IP-literal loopback issuer, matching Core's development rule.</summary>
    internal static bool IsAllowedIssuer(Uri issuer) =>
        issuer.Scheme == Uri.UriSchemeHttps ||
        (issuer.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(issuer.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
