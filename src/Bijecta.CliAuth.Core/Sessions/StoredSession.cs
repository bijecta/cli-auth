using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

/// <summary>A sensitive immutable envelope contract for advanced storage extensions.</summary>
/// <remarks>Does not contain a raw ID token. Mutation ordering belongs to the session coordinator.</remarks>
[DebuggerDisplay("StoredSession ({State}, redacted)")]
public sealed class StoredSession
{
    /// <summary>The only schema accepted by the current envelope model.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates a schema-checked envelope or credential-free lifecycle marker.</summary>
    public StoredSession(long generation, Guid epoch, SessionLifecycleState state,
        AccessToken? accessToken = null, string? refreshToken = null, Uri? issuer = null,
        string? clientId = null, string? subject = null, int schemaVersion = CurrentSchemaVersion)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new CliAuthException(AuthErrorCode.UnsupportedSchema);
        }
        if (generation < 0 || epoch == Guid.Empty || !Enum.IsDefined(state))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        var holdsCredentials = state is SessionLifecycleState.Active or SessionLifecycleState.RefreshInProgress;
        if (holdsCredentials)
        {
            if (accessToken is null)
            {
                throw new CliAuthException(AuthErrorCode.InvalidRequest);
            }
            Issuer = ContractValidation.Issuer(issuer, allowLoopbackHttp: true, AuthErrorCode.InvalidRequest);
            ClientId = ContractValidation.Identifier(clientId, AuthErrorCode.InvalidRequest);
            Subject = ContractValidation.Identifier(subject, AuthErrorCode.InvalidRequest);
        }
        else if (accessToken is not null || refreshToken is not null || issuer is not null || clientId is not null || subject is not null)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        if (refreshToken is not null)
        {
            ContractValidation.Identifier(refreshToken, AuthErrorCode.InvalidRequest);
        }
        Generation = generation;
        Epoch = epoch;
        State = state;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    /// <summary>The validated schema version.</summary>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>A monotonic mutation generation within the stored session key.</summary>
    public long Generation { get; }
    /// <summary>The session epoch checked by login/logout mutation coordination.</summary>
    public Guid Epoch { get; }
    /// <summary>The persisted lifecycle marker.</summary>
    public SessionLifecycleState State { get; }
    /// <summary>The sensitive access-token object, absent in credential-free markers.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public AccessToken? AccessToken { get; }
    /// <summary>The sensitive refresh token, exposed only to advanced storage/coordinator code.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? RefreshToken { get; }
    /// <summary>The exact issuer binding of an active session.</summary>
    public Uri? Issuer { get; }
    /// <summary>The client binding of an active session.</summary>
    public string? ClientId { get; }
    /// <summary>The validated subject; avoid including personal metadata in telemetry by default.</summary>
    public string? Subject { get; }
    /// <summary>Formats without tokens or account metadata.</summary>
    public override string ToString() => $"StoredSession ({State}, redacted)";
}
