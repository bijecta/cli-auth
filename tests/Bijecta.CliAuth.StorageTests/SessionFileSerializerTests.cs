using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

public sealed class SessionFileSerializerTests
{
    private static readonly SessionKey Key = new(new string('a', 64));
    private static readonly Guid Epoch = Guid.Parse("7348885f-33f4-43b0-b636-4c40eb5dd4a2");
    private static StoredSession Session(SessionLifecycleState state = SessionLifecycleState.Active, string? refresh = "synthetic-refresh") =>
        new(7, Epoch, state, new AccessToken("synthetic-access", new DateTimeOffset(2031, 1, 2, 12, 0, 0, TimeSpan.FromHours(2)), ["profile", "openid"]),
            refresh, new Uri("https://issuer.example.test/Realm/"), "sample-cli", "synthetic-subject");

    [Theory]
    [InlineData(SessionLifecycleState.Active, true)]
    [InlineData(SessionLifecycleState.Active, false)]
    [InlineData(SessionLifecycleState.RefreshInProgress, true)]
    public void CompactUtf8RoundTripPreservesCompleteSession(SessionLifecycleState state, bool refresh)
    {
        var session = Session(state, refresh ? "synthetic-refresh" : null);
        var bytes = SessionFileSerializer.Serialize(Key, session);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain("  ", text);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var result = SessionFileSerializer.Deserialize(Key, bytes);
        Assert.Equal(session.SchemaVersion, result.SchemaVersion);
        Assert.Equal(7, result.Generation);
        Assert.Equal(Epoch, result.Epoch);
        Assert.Equal(state, result.State);
        Assert.Equal(session.Issuer!.OriginalString, result.Issuer!.OriginalString);
        Assert.Equal(session.ClientId, result.ClientId);
        Assert.Equal(session.Subject, result.Subject);
        Assert.Equal("synthetic-access", result.AccessToken!.Value);
        Assert.Equal(session.AccessToken!.ExpiresAtUtc, result.AccessToken.ExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, result.AccessToken.ExpiresAtUtc.Offset);
        Assert.Equal(["openid", "profile"], result.AccessToken.GrantedScopes);
        Assert.Equal("Bearer", result.AccessToken.TokenType);
        Assert.Equal(session.RefreshToken, result.RefreshToken);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(Key.Value, json.RootElement.GetProperty("sessionKey").GetString());
        Assert.False(json.RootElement.TryGetProperty("idToken", out _));
        Assert.False(json.RootElement.TryGetProperty("profile", out _));
    }

    [Theory]
    [InlineData(SessionLifecycleState.None)]
    [InlineData(SessionLifecycleState.ReauthenticationRequired)]
    [InlineData(SessionLifecycleState.Uncertain)]
    public void CredentialFreeStatesRetainOnlyMutationMetadata(SessionLifecycleState state)
    {
        var bytes = SessionFileSerializer.Serialize(Key, new StoredSession(9, Epoch, state));
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(5, json.RootElement.EnumerateObject().Count());
        var result = SessionFileSerializer.Deserialize(Key, bytes);
        Assert.Equal(state, result.State);
        Assert.Equal(9, result.Generation);
        Assert.Equal(Epoch, result.Epoch);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.Subject);
        Assert.Null(result.Issuer);
        Assert.Null(result.ClientId);
    }

    [Fact]
    public void IntentionalFileSerializationDoesNotChangeCoreDiagnosticRedaction()
    {
        var session = Session();
        var file = Encoding.UTF8.GetString(SessionFileSerializer.Serialize(Key, session));
        Assert.Contains("synthetic-access", file);
        Assert.Contains("synthetic-refresh", file);
        Assert.DoesNotContain("synthetic-access", JsonSerializer.Serialize(session.AccessToken));
        Assert.DoesNotContain("synthetic-refresh", JsonSerializer.Serialize(session));
        Assert.DoesNotContain("synthetic", session.ToString());
    }

    [Fact]
    public void AnotherSessionKeyCannotReadTheEnvelope()
    {
        var bytes = SessionFileSerializer.Serialize(Key, Session());
        AssertCorrupt(() => SessionFileSerializer.Deserialize(new SessionKey(new string('b', 64)), bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedSchemaIsReportedWithoutTryingToLoadCredentials(int version)
    {
        var document = Document();
        document["schemaVersion"] = version;
        document["futureField"] = "synthetic-future-content";
        var error = Assert.Throws<CliAuthException>(() => Decode(document));
        Assert.Equal(AuthErrorCode.UnsupportedSchema, error.ErrorCode);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("missing_version")]
    [InlineData("missing_generation")]
    [InlineData("negative_generation")]
    [InlineData("empty_epoch")]
    [InlineData("numeric_state")]
    [InlineData("unknown_state")]
    [InlineData("missing_access")]
    [InlineData("missing_expiry")]
    [InlineData("wrong_type")]
    [InlineData("expiry_no_offset")]
    [InlineData("expiry_non_utc")]
    [InlineData("missing_issuer")]
    [InlineData("null_refresh")]
    [InlineData("unknown_field")]
    [InlineData("marker_credentials")]
    public void InvalidEnvelopesFailWithoutRawDiagnostics(string scenario)
    {
        var document = Document();
        switch (scenario)
        {
            case "missing_version": document.Remove("schemaVersion"); break;
            case "missing_generation": document.Remove("generation"); break;
            case "negative_generation": document["generation"] = -1; break;
            case "empty_epoch": document["epoch"] = Guid.Empty; break;
            case "numeric_state": document["state"] = "1"; break;
            case "unknown_state": document["state"] = "synthetic-secret-state"; break;
            case "missing_access": document.Remove("accessToken"); break;
            case "missing_expiry": document["accessToken"]!.AsObject().Remove("expiresAtUtc"); break;
            case "wrong_type": document["accessToken"]!["tokenType"] = "DPoP"; break;
            case "expiry_no_offset": document["accessToken"]!["expiresAtUtc"] = "2031-01-02T12:00:00"; break;
            case "expiry_non_utc": document["accessToken"]!["expiresAtUtc"] = "2031-01-02T12:00:00+02:00"; break;
            case "missing_issuer": document.Remove("issuer"); break;
            case "null_refresh": document["refreshToken"] = null; break;
            case "unknown_field": document["idToken"] = "synthetic-id-token"; break;
            default: document["state"] = "None"; break;
        }
        AssertCorrupt(() => Decode(document));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":2}")]
    public void MalformedOrAmbiguousJsonIsRejected(string text) =>
        AssertCorrupt(() => SessionFileSerializer.Deserialize(Key, Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void DuplicateTokenPropertiesAreRejected()
    {
        var text = Encoding.UTF8.GetString(SessionFileSerializer.Serialize(Key, Session()));
        text = text.Replace("\"value\":\"synthetic-access\"", "\"value\":\"synthetic-access\",\"value\":\"synthetic-second\"", StringComparison.Ordinal);
        AssertCorrupt(() => SessionFileSerializer.Deserialize(Key, Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void DocumentSizeIsBoundedInBothDirections()
    {
        AssertCorrupt(() => SessionFileSerializer.Deserialize(Key, new byte[SessionFileSerializer.MaxDocumentBytes + 1]));
        var large = new StoredSession(0, Epoch, SessionLifecycleState.Active,
            new AccessToken(new string('x', SessionFileSerializer.MaxDocumentBytes), DateTimeOffset.UtcNow),
            issuer: new Uri("https://issuer.example.test"), clientId: "cli", subject: "subject");
        Assert.Equal(AuthErrorCode.InvalidRequest,
            Assert.Throws<CliAuthException>(() => SessionFileSerializer.Serialize(Key, large)).ErrorCode);
    }

    private static JsonObject Document() => JsonNode.Parse(SessionFileSerializer.Serialize(Key, Session()))!.AsObject();
    private static StoredSession Decode(JsonObject document) =>
        SessionFileSerializer.Deserialize(Key, JsonSerializer.SerializeToUtf8Bytes(document));

    private static void AssertCorrupt(Action action)
    {
        var error = Assert.Throws<CliAuthException>(action);
        Assert.Equal(AuthErrorCode.StorageCorrupt, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString());
    }
}
