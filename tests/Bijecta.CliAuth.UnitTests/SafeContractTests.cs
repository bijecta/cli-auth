using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class SafeContractTests
{
    [Fact]
    public void TokenAndEnvelopeFormattingAndDefaultSerializationRedactCredentials()
    {
        var session = CoreTestData.Session();
        var token = session.AccessToken!;
        Assert.Equal("synthetic-access", token.Value);
        Assert.Equal("synthetic-refresh", session.RefreshToken);
        foreach (var output in new[] { token.ToString(), session.ToString(), JsonSerializer.Serialize(token), JsonSerializer.Serialize(session) })
        {
            Assert.DoesNotContain("synthetic-access", output);
            Assert.DoesNotContain("synthetic-refresh", output);
        }
        Assert.NotEqual(token, new AccessToken(token.Value, token.ExpiresAtUtc, token.GrantedScopes));
        Assert.Equal(DebuggerBrowsableState.Never,
            typeof(AccessToken).GetProperty(nameof(AccessToken.Value))!.GetCustomAttribute<DebuggerBrowsableAttribute>()!.State);
        Assert.Equal(DebuggerBrowsableState.Never,
            typeof(StoredSession).GetProperty(nameof(StoredSession.RefreshToken))!.GetCustomAttribute<DebuggerBrowsableAttribute>()!.State);
    }

    [Fact]
    public void GrantedScopesAreDefensiveAndDoNotInventOpenId()
    {
        var scopes = new List<string> { "api.read" };
        var token = new AccessToken("synthetic-token", CoreTestData.Now, scopes, "bearer");
        scopes.Clear();
        Assert.Equal(["api.read"], token.GrantedScopes);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Throws<CliAuthException>(() => new AccessToken("synthetic-token", CoreTestData.Now, tokenType: "DPoP"));
        Assert.Throws<CliAuthException>(() => new AccessToken("bad\r\nheader", CoreTestData.Now));
    }

    [Fact]
    public void DevicePresentationDoesNotExposeCodeBearingDataThroughDiagnostics()
    {
        var prompt = new DeviceAuthorizationPrompt("USER-CODE", new Uri("https://issuer.example.test/verify"),
            CoreTestData.Now.AddMinutes(5), new Uri("https://issuer.example.test/verify?user_code=USER-CODE"));
        Assert.Equal("USER-CODE", prompt.UserCode);
        Assert.NotNull(prompt.VerificationUriComplete);
        foreach (var output in new[] { prompt.ToString(), JsonSerializer.Serialize(prompt) })
        {
            Assert.DoesNotContain("USER-CODE", output);
            Assert.DoesNotContain("verify", output);
        }
        Assert.DoesNotContain(typeof(DeviceAuthorizationPrompt).GetProperties(), property => property.Name == "DeviceCode");
        Assert.Throws<CliAuthException>(() => new DeviceAuthorizationPrompt(
            "USER-CODE", new Uri("http://issuer.example.test/verify"), CoreTestData.Now));
        Assert.Throws<CliAuthException>(() => new DeviceAuthorizationPrompt(
            "USER-CODE", new Uri("https://user:password@issuer.example.test/verify"), CoreTestData.Now));
    }

    [Fact]
    public void ExceptionsDiscardUnknownProviderTextAndHaveSafeStableMessages()
    {
        var failure = new CliAuthException(AuthErrorCode.ProtocolFailure, "synthetic-secret-from-body");
        Assert.Null(failure.ProviderErrorCode);
        Assert.Null(failure.InnerException);
        Assert.NotEqual(Guid.Empty, failure.CorrelationId);
        Assert.DoesNotContain("synthetic-secret-from-body", failure.ToString());
        Assert.Equal("invalid_grant", new CliAuthException(AuthErrorCode.ReauthenticationRequired, "invalid_grant").ProviderErrorCode);
        Assert.NotEmpty(failure.RetryGuidance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnknownSchemasFailClosed(int schema)
    {
        var failure = Assert.Throws<CliAuthException>(() => new StoredSession(
            0, Guid.NewGuid(), SessionLifecycleState.None, schemaVersion: schema));
        Assert.Equal(AuthErrorCode.UnsupportedSchema, failure.ErrorCode);
    }

    [Fact]
    public void LifecycleMarkersCannotCarryCredentialsOrHaveMissingMutationIdentity()
    {
        Assert.Throws<CliAuthException>(() => new StoredSession(0, Guid.Empty, SessionLifecycleState.None));
        Assert.Throws<CliAuthException>(() => new StoredSession(-1, Guid.NewGuid(), SessionLifecycleState.None));
        Assert.Throws<CliAuthException>(() => new StoredSession(0, Guid.NewGuid(), SessionLifecycleState.Active));
        Assert.Throws<CliAuthException>(() => new StoredSession(0, Guid.NewGuid(), SessionLifecycleState.Uncertain,
            refreshToken: "synthetic-refresh"));
    }
}
