using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class SessionIsolationTests
{
    [Fact]
    public void ScopeOrderingAndDuplicatesDoNotCreateDifferentSessions()
    {
        var first = CoreTestData.Profile(scopes: ["b", "a", "a"]);
        var second = CoreTestData.Profile(scopes: ["openid", "a", "b"]);
        Assert.Equal(SessionKey.ForProfile(first), SessionKey.ForProfile(second));
    }

    [Fact]
    public void IdentityAndSecurityPolicyChangesAreIsolated()
    {
        var baseline = SessionKey.ForProfile(CoreTestData.Profile());
        OidcProfile[] alternatives =
        [
            CoreTestData.Profile(application: "another.cli"),
            CoreTestData.Profile(name: "other"),
            CoreTestData.Profile(issuer: "https://issuer.example.test/"),
            CoreTestData.Profile(issuer: "https://issuer.example.test/Realm"),
            CoreTestData.Profile(issuer: "https://issuer.example.test/realm"),
            CoreTestData.Profile(client: "other-client"),
            CoreTestData.Profile(scopes: ["api.read"]),
            CoreTestData.Profile(scopes: ["API.read"]),
            CoreTestData.Profile(callback: new BrowserCallbackOptions(43821)),
            CoreTestData.Profile(callback: new BrowserCallbackOptions(path: "/other/")),
            CoreTestData.Profile(allowLoopbackHttp: true),
            CoreTestData.Profile(margin: TimeSpan.Zero),
            CoreTestData.Profile(persistence: PersistenceMode.Protected)
        ];
        var keys = alternatives.Select(SessionKey.ForProfile).ToArray();
        Assert.DoesNotContain(baseline, keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void LengthPrefixesPreventIdentifierConcatenationCollisions()
    {
        Assert.NotEqual(
            SessionKey.ForProfile(CoreTestData.Profile(application: "ab", name: "c")),
            SessionKey.ForProfile(CoreTestData.Profile(application: "a", name: "bc")));
    }

    [Fact]
    public void KeyContainsNoRawConfigurationAndCanBeReconstructed()
    {
        var key = SessionKey.ForProfile(CoreTestData.Profile());
        Assert.Equal(64, key.Value.Length);
        Assert.DoesNotContain("issuer", key.ToString());
        Assert.DoesNotContain("sample", key.ToString());
        Assert.Equal(key, new SessionKey(key.Value.ToUpperInvariant()));
        Assert.Throws<CliAuthException>(() => new SessionKey("../../session"));
    }
}
