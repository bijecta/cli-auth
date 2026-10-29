using System.Text.Json;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Storage.File;

// Intentional credential serialization lives only in the file package. Core's JsonIgnore safeguards remain intact.
internal static class SessionFileSerializer
{
    internal const int MaxDocumentBytes = 1024 * 1024;
    private static readonly HashSet<string> CommonFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "sessionKey", "generation", "epoch", "state"
    };
    private static readonly HashSet<string> CredentialFields = new(CommonFields, StringComparer.Ordinal)
    {
        "issuer", "clientId", "subject", "accessToken", "refreshToken"
    };
    private static readonly HashSet<string> TokenFields = new(StringComparer.Ordinal)
    {
        "value", "expiresAtUtc", "grantedScopes", "tokenType"
    };

    internal static byte[] Serialize(SessionKey key, StoredSession session)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", session.SchemaVersion);
            writer.WriteString("sessionKey", key.Value);
            writer.WriteNumber("generation", session.Generation);
            writer.WriteString("epoch", session.Epoch);
            writer.WriteString("state", session.State.ToString());
            if (session.AccessToken is { } token)
            {
                writer.WriteString("issuer", session.Issuer!.OriginalString);
                writer.WriteString("clientId", session.ClientId);
                writer.WriteString("subject", session.Subject);
                writer.WriteStartObject("accessToken");
                writer.WriteString("value", token.Value);
                writer.WriteString("expiresAtUtc", token.ExpiresAtUtc);
                writer.WriteStartArray("grantedScopes");
                foreach (var scope in token.GrantedScopes) { writer.WriteStringValue(scope); }
                writer.WriteEndArray();
                writer.WriteString("tokenType", token.TokenType);
                writer.WriteEndObject();
                if (session.RefreshToken is not null) { writer.WriteString("refreshToken", session.RefreshToken); }
            }
            writer.WriteEndObject();
        }
        if (stream.Length > MaxDocumentBytes) { throw new CliAuthException(AuthErrorCode.InvalidRequest); }
        return stream.ToArray();
    }

    internal static StoredSession Deserialize(SessionKey expectedKey, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(expectedKey);
        if (bytes.Length == 0 || bytes.Length > MaxDocumentBytes) { throw Corrupt(); }
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            // Inspect names before selecting a schema; duplicate version/state fields cannot change interpretation.
            CheckObject(root);
            var version = root.GetProperty("schemaVersion").GetInt32();
            if (version != StoredSession.CurrentSchemaVersion)
            {
                throw new CliAuthException(AuthErrorCode.UnsupportedSchema);
            }
            CheckObject(root, CredentialFields);
            if (RequiredString(root, "sessionKey") != expectedKey.Value) { throw Corrupt(); }
            var generation = root.GetProperty("generation").GetInt64();
            var epoch = root.GetProperty("epoch").GetGuid();
            var stateText = RequiredString(root, "state");
            if (!Enum.TryParse<SessionLifecycleState>(stateText, out var state) ||
                !Enum.IsDefined(state) || state.ToString() != stateText)
            {
                throw Corrupt();
            }
            if (state is not (SessionLifecycleState.Active or SessionLifecycleState.RefreshInProgress))
            {
                CheckObject(root, CommonFields);
                return new StoredSession(generation, epoch, state);
            }
            var access = root.GetProperty("accessToken");
            CheckObject(access, TokenFields);
            var scopes = access.GetProperty("grantedScopes").EnumerateArray()
                .Select(scope => scope.GetString() ?? throw Corrupt()).ToArray();
            var expiryText = RequiredString(access, "expiresAtUtc");
            if (!expiryText.EndsWith("Z", StringComparison.Ordinal) && !expiryText.EndsWith("+00:00", StringComparison.Ordinal))
            {
                throw Corrupt();
            }
            var token = new AccessToken(RequiredString(access, "value"),
                access.GetProperty("expiresAtUtc").GetDateTimeOffset(), scopes, RequiredString(access, "tokenType"));
            return new StoredSession(generation, epoch, state, token,
                root.TryGetProperty("refreshToken", out _) ? RequiredString(root, "refreshToken") : null,
                new Uri(RequiredString(root, "issuer"), UriKind.Absolute),
                RequiredString(root, "clientId"), RequiredString(root, "subject"), version);
        }
        catch (CliAuthException error) when (error.ErrorCode == AuthErrorCode.UnsupportedSchema) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or ArgumentException or OverflowException or CliAuthException)
        {
            // Raw parsing/constructor exceptions can contain credentials or personal metadata.
            throw Corrupt();
        }
    }

    private static void CheckObject(JsonElement value, HashSet<string>? allowed = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (allowed is not null && !allowed.Contains(property.Name) || !seen.Add(property.Name)) { throw Corrupt(); }
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw Corrupt();

    private static CliAuthException Corrupt() => new(AuthErrorCode.StorageCorrupt);
}
