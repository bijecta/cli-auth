using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

/// <summary>Presentation-only device authorization data; never contains the device code.</summary>
[DebuggerDisplay("DeviceAuthorizationPrompt (redacted)")]
public sealed class DeviceAuthorizationPrompt
{
    /// <summary>Creates presentation data after protocol response validation.</summary>
    public DeviceAuthorizationPrompt(string userCode, Uri verificationUri, DateTimeOffset expiresAtUtc,
        Uri? verificationUriComplete = null, bool allowLoopbackHttp = false)
    {
        UserCode = ContractValidation.Identifier(userCode, AuthErrorCode.ProtocolFailure);
        VerificationUri = ValidateUri(verificationUri, allowLoopbackHttp);
        VerificationUriComplete = verificationUriComplete is null ? null : ValidateUri(verificationUriComplete, allowLoopbackHttp);
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    /// <summary>The short user code for intentional host presentation.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string UserCode { get; }
    /// <summary>The provider's verification page; excluded from ordinary serialization.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public Uri VerificationUri { get; }
    /// <summary>An optional code-bearing URI for intentional presentation.</summary>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public Uri? VerificationUriComplete { get; }
    /// <summary>The absolute provider expiry, normalized to UTC.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }
    /// <summary>Formats without codes or verification URIs.</summary>
    public override string ToString() => "DeviceAuthorizationPrompt (redacted)";

    private static Uri ValidateUri(Uri? uri, bool allowLoopbackHttp)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.OriginalString.Contains('#') || uri.OriginalString.Any(char.IsWhiteSpace) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && ContractValidation.IsLiteralLoopback(uri))))
        {
            throw new CliAuthException(AuthErrorCode.ProtocolFailure);
        }
        return uri;
    }
}
