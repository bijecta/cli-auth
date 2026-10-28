using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

// A protocol adapter must complete cryptographic and flow validation before returning this result.
[DebuggerDisplay("ValidatedLogin (redacted)")]
internal sealed class ValidatedLogin(AccessToken accessToken, string subject, Uri issuer,
    string clientId, string? refreshToken = null)
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal AccessToken AccessToken { get; } = accessToken;
    internal string Subject { get; } = subject;
    internal Uri Issuer { get; } = issuer;
    internal string ClientId { get; } = clientId;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? RefreshToken { get; } = refreshToken;
    public override string ToString() => "ValidatedLogin (redacted)";
}
