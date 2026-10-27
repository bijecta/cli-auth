using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bijecta.CliAuth.Core;

/// <summary>A non-secret SHA-256 identifier isolating an application profile and security policy.</summary>
public sealed record SessionKey
{
    /// <summary>Reconstructs a key from its hexadecimal representation for storage extensions.</summary>
    public SessionKey(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        Value = value.ToLowerInvariant();
    }

    /// <summary>The lowercase hexadecimal key; this is not encryption.</summary>
    public string Value { get; }

    /// <summary>Creates a stable key using length-prefixed UTF-8 fields and exact issuer spelling.</summary>
    public static SessionKey ForProfile(OidcProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var policy = Hash([
            "Bijecta.CliAuth.Policy/v1", profile.Callback.Address,
            profile.Callback.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.Callback.Path,
            profile.AllowLoopbackHttp ? "allow-loopback-http" : "https-only",
            profile.ExpiryMargin.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.Persistence.ToString(), "id-token-algorithms:RS256,ES256", "validation-skew-seconds:60"
        ]);
        var fields = new List<string>
        {
            "Bijecta.CliAuth.Session/v1", profile.ApplicationId, profile.ProfileName,
            profile.Issuer.OriginalString, profile.ClientId
        };
        fields.AddRange(profile.Scopes);
        // Include an explicit scope count to keep the boundary before policy unambiguous.
        fields.Insert(5, profile.Scopes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.Add(policy);
        return new SessionKey(Hash(fields));
    }

    /// <summary>Formats the non-secret key without profile or issuer metadata.</summary>
    public override string ToString() => Value;

    private static string Hash(IEnumerable<string> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
