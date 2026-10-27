namespace Bijecta.CliAuth.Core;

/// <summary>Immutable, locally validated provider and session-isolation configuration.</summary>
public sealed class OidcProfile
{
    /// <summary>Creates a profile without discovery, storage access, or other network I/O.</summary>
    public OidcProfile(string applicationId, string profileName, Uri issuer, string clientId,
        IEnumerable<string>? scopes = null, PersistenceMode persistence = PersistenceMode.Protected,
        BrowserCallbackOptions? callback = null, bool allowLoopbackHttp = false, TimeSpan? expiryMargin = null)
    {
        ApplicationId = ContractValidation.Identifier(applicationId);
        ProfileName = ContractValidation.Identifier(profileName);
        Issuer = ContractValidation.Issuer(issuer, allowLoopbackHttp);
        ClientId = ContractValidation.Identifier(clientId);
        if (!Enum.IsDefined(persistence))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }

        ExpiryMargin = expiryMargin ?? TimeSpan.FromSeconds(60);
        if (ExpiryMargin < TimeSpan.Zero || ExpiryMargin > TimeSpan.FromSeconds(300))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }

        Scopes = ContractValidation.Scopes(scopes, includeOpenId: true);
        Persistence = persistence;
        Callback = callback ?? new BrowserCallbackOptions();
        AllowLoopbackHttp = allowLoopbackHttp;
    }

    /// <summary>The application's non-secret storage namespace.</summary>
    public string ApplicationId { get; }
    /// <summary>The case-sensitive name used by facade operations.</summary>
    public string ProfileName { get; }
    /// <summary>The exact configured issuer; OriginalString preserves its identity spelling.</summary>
    public Uri Issuer { get; }
    /// <summary>The native public client's identifier; no client secret is accepted.</summary>
    public string ClientId { get; }
    /// <summary>A defensive, sorted, case-sensitive scope set including openid.</summary>
    public IReadOnlyList<string> Scopes { get; }
    /// <summary>The required persistence policy, independent of backend selection.</summary>
    public PersistenceMode Persistence { get; }
    /// <summary>Browser-only callback settings; device flow does not bind a listener.</summary>
    public BrowserCallbackOptions Callback { get; }
    /// <summary>An explicit development exception for IP-literal loopback HTTP endpoints.</summary>
    public bool AllowLoopbackHttp { get; }
    /// <summary>The conservative margin used for cached-token availability.</summary>
    public TimeSpan ExpiryMargin { get; }
}
