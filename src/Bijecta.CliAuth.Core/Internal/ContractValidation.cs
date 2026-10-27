using System.Net;
using System.Text;

namespace Bijecta.CliAuth.Core;

internal static class ContractValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Identifier(string? value, AuthErrorCode code = AuthErrorCode.InvalidConfiguration)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new CliAuthException(code);
        }

        try
        {
            StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new CliAuthException(code);
        }

        return value;
    }

    internal static Uri Issuer(Uri? issuer, bool allowLoopbackHttp, AuthErrorCode code = AuthErrorCode.InvalidConfiguration)
    {
        if (issuer is null || !issuer.IsAbsoluteUri || issuer.UserInfo.Length != 0 ||
            issuer.OriginalString.IndexOfAny(['?', '#']) >= 0 || issuer.OriginalString.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            (issuer.Scheme != Uri.UriSchemeHttps &&
             !(allowLoopbackHttp && issuer.Scheme == Uri.UriSchemeHttp && IsLiteralLoopback(issuer))))
        {
            throw new CliAuthException(code);
        }

        return issuer;
    }

    internal static bool IsLiteralLoopback(Uri uri) =>
        IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    internal static IReadOnlyList<string> Scopes(IEnumerable<string>? scopes, bool includeOpenId,
        AuthErrorCode code = AuthErrorCode.InvalidConfiguration)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        if (includeOpenId)
        {
            values.Add("openid");
        }

        foreach (var scope in scopes ?? [])
        {
            // OAuth scope-token is printable ASCII excluding space, quote and backslash.
            if (string.IsNullOrEmpty(scope) || scope.Any(c => c < 0x21 || c > 0x7e || c is '"' or '\\'))
            {
                throw new CliAuthException(code);
            }
            values.Add(scope);
        }

        return Array.AsReadOnly(values.ToArray());
    }
}
