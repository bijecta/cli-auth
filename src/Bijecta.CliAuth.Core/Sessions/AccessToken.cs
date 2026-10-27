using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

/// <summary>An opaque bearer token with explicit sensitive-value access.</summary>
[DebuggerDisplay("AccessToken (redacted)")]
public sealed class AccessToken
{
    /// <summary>Creates a token without decoding its value or inferring identity or expiry.</summary>
    public AccessToken(string value, DateTimeOffset expiresAtUtc, IEnumerable<string>? grantedScopes = null,
        string tokenType = "Bearer")
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Value = value;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        GrantedScopes = ContractValidation.Scopes(grantedScopes, includeOpenId: false, AuthErrorCode.InvalidRequest);
    }

    /// <summary>The credential; read only for intentional protocol use and never log it.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Value { get; }
    /// <summary>Expiry derived from the provider's response, normalized to UTC.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }
    /// <summary>The granted scopes, without inventing missing grants.</summary>
    public IReadOnlyList<string> GrantedScopes { get; }
    /// <summary>The canonical bearer token type.</summary>
    public string TokenType => "Bearer";
    /// <summary>Formats without including the credential.</summary>
    public override string ToString() => "AccessToken (redacted)";
}
