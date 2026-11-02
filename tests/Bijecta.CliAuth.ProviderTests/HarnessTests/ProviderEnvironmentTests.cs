using Bijecta.CliAuth.ProviderTests.Harness;

namespace Bijecta.CliAuth.ProviderTests.HarnessTests;

// The skip policy and redaction rules, without a browser or a provider.
public sealed class ProviderEnvironmentTests
{
    private static readonly ProviderDefinition Sample = new("Sample", "SAMPLE", RefreshRotation.Strict,
        CanDenyConsent: true, ReusesProviderSession: true, DefaultScopes: ["profile", "offline_access"], RequiredExtras: ["AUDIENCE"]);

    private const string SecretIssuer = "https://tenant-7f3a.example.test/oauth2/default";
    private const string SecretPassword = "pw-should-never-print";

    private static Dictionary<string, string?> Complete() => new()
    {
        ["CLIAUTH_SAMPLE_ISSUER"] = SecretIssuer,
        ["CLIAUTH_SAMPLE_CLIENT_ID"] = "client-0oa123",
        ["CLIAUTH_SAMPLE_USERNAME"] = "user@example.test",
        ["CLIAUTH_SAMPLE_PASSWORD"] = SecretPassword,
        ["CLIAUTH_SAMPLE_CONSENT_CLIENT_ID"] = "consent-0oa456",
        ["CLIAUTH_SAMPLE_AUDIENCE"] = "api://audience-secret",
    };

    private static ProviderResolution Read(Dictionary<string, string?> variables) =>
        ProviderEnvironment.Read(Sample, name => variables.GetValueOrDefault(name));

    [Fact]
    public void NoVariablesSkipsWithReasonNamingTheVariables()
    {
        var result = Read([]);
        Assert.Equal(ProviderAvailability.NotConfigured, result.Availability);
        Assert.Null(result.Settings);
        Assert.Contains("CLIAUTH_SAMPLE_ISSUER", result.Reason);
        Assert.Contains("CLIAUTH_SAMPLE_CONSENT_CLIENT_ID", result.Reason);
        Assert.Contains("CLIAUTH_SAMPLE_AUDIENCE", result.Reason);
    }

    [Fact]
    public void BlankValuesCountAsUnset()
    {
        var variables = Complete().ToDictionary(pair => pair.Key, _ => (string?)"  ");
        Assert.Equal(ProviderAvailability.NotConfigured, Read(variables).Availability);
    }

    [Theory]
    [InlineData("Sample")]
    [InlineData("okta, sample")]
    [InlineData("SAMPLE")]
    public void RequiredProviderWithoutVariablesFails(string required)
    {
        var result = Read(new() { ["CLIAUTH_REQUIRED_PROVIDERS"] = required });
        Assert.Equal(ProviderAvailability.Misconfigured, result.Availability);
        Assert.Contains("CLIAUTH_REQUIRED_PROVIDERS", result.Reason);
    }

    [Fact]
    public void OtherRequiredProvidersDoNotAffectThisOne()
    {
        Assert.Equal(ProviderAvailability.NotConfigured, Read(new() { ["CLIAUTH_REQUIRED_PROVIDERS"] = "Okta,Auth0" }).Availability);
    }

    [Theory]
    [InlineData("CLIAUTH_SAMPLE_PASSWORD")]
    [InlineData("CLIAUTH_SAMPLE_CONSENT_CLIENT_ID")]
    [InlineData("CLIAUTH_SAMPLE_AUDIENCE")]
    public void PartialConfigurationFailsAndNamesTheMissingVariable(string missing)
    {
        var variables = Complete();
        variables.Remove(missing);
        var result = Read(variables);
        Assert.Equal(ProviderAvailability.Misconfigured, result.Availability);
        Assert.Contains(missing, result.Reason);
    }

    [Fact]
    public void OnlyAnOptionalVariableSetCountsAsPartialConfiguration()
    {
        var result = Read(new() { ["CLIAUTH_SAMPLE_CALLBACK_PORT"] = "43821" });
        Assert.Equal(ProviderAvailability.Misconfigured, result.Availability);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("http://tenant.example.test/")]
    [InlineData("http://localhost:8080/realms/x")]
    [InlineData("https://tenant.example.test/?x=1")]
    [InlineData("https://tenant.example.test/#f")]
    [InlineData("ftp://tenant.example.test/")]
    public void InvalidIssuerFailsWithoutEchoingTheValue(string issuer)
    {
        var variables = Complete();
        variables["CLIAUTH_SAMPLE_ISSUER"] = issuer;
        var result = Read(variables);
        Assert.Equal(ProviderAvailability.Misconfigured, result.Availability);
        Assert.Contains("CLIAUTH_SAMPLE_ISSUER", result.Reason);
        Assert.DoesNotContain(issuer, result.Reason);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/realms/cliauth")]
    [InlineData("http://[::1]:8080/realms/cliauth")]
    public void PlainHttpIsAcceptedOnlyForIpLiteralLoopback(string issuer)
    {
        var variables = Complete();
        variables["CLIAUTH_SAMPLE_ISSUER"] = issuer;
        Assert.Equal(ProviderAvailability.Ready, Read(variables).Availability);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("80a")]
    [InlineData("+80")]
    public void InvalidCallbackPortFails(string port)
    {
        var variables = Complete();
        variables["CLIAUTH_SAMPLE_CALLBACK_PORT"] = port;
        var result = Read(variables);
        Assert.Equal(ProviderAvailability.Misconfigured, result.Availability);
        Assert.Contains("CLIAUTH_SAMPLE_CALLBACK_PORT", result.Reason);
    }

    [Fact]
    public void CompleteConfigurationUsesDefaultsForOptionalVariables()
    {
        var result = Read(Complete());
        Assert.Equal(ProviderAvailability.Ready, result.Availability);
        var settings = result.Settings!;
        Assert.Equal(new Uri(SecretIssuer), settings.Issuer);
        Assert.Equal("client-0oa123", settings.ClientId);
        Assert.Equal("client-0oa123", settings.DeviceClientId);
        Assert.Equal("consent-0oa456", settings.ConsentClientId);
        Assert.Equal(0, settings.CallbackPort);
        Assert.Equal(["profile", "offline_access"], settings.Scopes);
        Assert.Equal("api://audience-secret", settings.Extras["AUDIENCE"]);
    }

    [Fact]
    public void OptionalVariablesOverrideDefaults()
    {
        var variables = Complete();
        variables["CLIAUTH_SAMPLE_DEVICE_CLIENT_ID"] = " device-0oa789 ";
        variables["CLIAUTH_SAMPLE_CALLBACK_PORT"] = "43821";
        variables["CLIAUTH_SAMPLE_SCOPES"] = "email  offline_access";
        var settings = Read(variables).Settings!;
        Assert.Equal("device-0oa789", settings.DeviceClientId);
        Assert.Equal(43821, settings.CallbackPort);
        Assert.Equal(["email", "offline_access"], settings.Scopes);
    }

    [Fact]
    public void ConsentClientIsNotRequiredWhenTheProviderCannotDenyConsent()
    {
        var variables = Complete();
        variables.Remove("CLIAUTH_SAMPLE_CONSENT_CLIENT_ID");
        var provider = Sample with { CanDenyConsent = false };
        Assert.Equal(ProviderAvailability.Ready, ProviderEnvironment.Read(provider, name => variables.GetValueOrDefault(name)).Availability);
    }

    [Fact]
    public void SettingsAndReasonsNeverContainValues()
    {
        var variables = Complete();
        var settings = Read(variables).Settings!;
        var printed = settings.ToString();
        foreach (var value in variables.Values)
        {
            Assert.DoesNotContain(value!, printed);
        }
        variables.Remove("CLIAUTH_SAMPLE_USERNAME");
        var reason = Read(variables).Reason;
        foreach (var value in variables.Values)
        {
            Assert.DoesNotContain(value!, reason);
        }
    }

    [Fact]
    public void UnknownRequiredProviderNamesAreReported()
    {
        var unknown = ProviderEnvironment.UnknownRequiredProviders([Sample],
            name => name == "CLIAUTH_REQUIRED_PROVIDERS" ? "sample, Okat" : null);
        Assert.Equal(["Okat"], unknown);
    }

    // Guards the hosted workflow: a typo in CLIAUTH_REQUIRED_PROVIDERS must fail rather than run nothing.
    [Fact]
    public void RequiredProvidersInThisEnvironmentAreKnown()
    {
        Assert.Empty(ProviderEnvironment.UnknownRequiredProviders(ProviderCatalog.All, Environment.GetEnvironmentVariable));
    }
}
