using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

/// <summary>An opaque bearer token with explicit sensitive-value access.</summary>
[DebuggerDisplay("AccessToken (redacted)")]
public sealed class AccessToken
{
    /// <summary>Creates a token without decoding its value or inferring identity or expiry.</summary>
    public AccessToken(string value, DateTimeOffset expiresAtUtc, IEnumerable<string>? grantedScopes = null,
        string tokenType = "Bearer", DateTimeOffset? obtainedAtUtc = null)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || obtainedAtUtc > expiresAtUtc)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Value = value;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        ObtainedAtUtc = obtainedAtUtc?.ToUniversalTime();
        GrantedScopes = ContractValidation.Scopes(grantedScopes, includeOpenId: false, AuthErrorCode.InvalidRequest);
    }

    /// <summary>The credential; read only for intentional protocol use and never log it.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Value { get; }
    /// <summary>Expiry derived from the provider's response, normalized to UTC.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }
    /// <summary>Local time the token response was received, when known; bounds early refresh to the token's lifetime.</summary>
    public DateTimeOffset? ObtainedAtUtc { get; }
    /// <summary>The granted scopes, without inventing missing grants.</summary>
    public IReadOnlyList<string> GrantedScopes { get; }
    /// <summary>The canonical bearer token type.</summary>
    public string TokenType => "Bearer";
    /// <summary>Formats without including the credential.</summary>
    public override string ToString() => "AccessToken (redacted)";
}
