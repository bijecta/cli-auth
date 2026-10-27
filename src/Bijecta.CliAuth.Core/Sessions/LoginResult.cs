namespace Bijecta.CliAuth.Core;

/// <summary>Safe metadata returned after a protocol adapter validates a login.</summary>
public sealed class LoginResult
{
    /// <summary>Creates login metadata; no raw ID or refresh token is accepted.</summary>
    public LoginResult(string subject, Uri issuer, DateTimeOffset expiresAtUtc,
        IEnumerable<string> grantedScopes, PersistenceMode persistence)
    {
        Subject = ContractValidation.Identifier(subject, AuthErrorCode.InvalidRequest);
        Issuer = ContractValidation.Issuer(issuer, allowLoopbackHttp: true, AuthErrorCode.InvalidRequest);
        if (!Enum.IsDefined(persistence))
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        GrantedScopes = ContractValidation.Scopes(grantedScopes, includeOpenId: false, AuthErrorCode.InvalidRequest);
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        Persistence = persistence;
    }

    /// <summary>The validated subject identifier.</summary>
    public string Subject { get; }
    /// <summary>The exact validated issuer binding.</summary>
    public Uri Issuer { get; }
    /// <summary>The conservatively computed access-token expiry.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }
    /// <summary>The granted scopes.</summary>
    public IReadOnlyList<string> GrantedScopes { get; }
    /// <summary>The explicitly selected persistence policy.</summary>
    public PersistenceMode Persistence { get; }
    /// <summary>Formats without personal metadata.</summary>
    public override string ToString() => "LoginResult (metadata)";
}
