using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

// A protocol adapter must validate the token response (and any ID token's subject) before returning this result.
[DebuggerDisplay("ValidatedRefresh (redacted)")]
internal sealed class ValidatedRefresh(AccessToken accessToken, string? refreshToken = null)
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal AccessToken AccessToken { get; } = accessToken;
    // Null when the provider did not rotate; the stored refresh token is kept.
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? RefreshToken { get; } = refreshToken;
    public override string ToString() => "ValidatedRefresh (redacted)";
}
