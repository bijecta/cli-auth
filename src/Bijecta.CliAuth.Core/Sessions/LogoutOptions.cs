namespace Bijecta.CliAuth.Core;

/// <summary>Local logout policy with optional best-effort remote revocation.</summary>
public sealed class LogoutOptions
{
    /// <summary>Creates options; remote revocation is off by default.</summary>
    public LogoutOptions(bool revokeRefreshToken = false) => RevokeRefreshToken = revokeRefreshToken;
    /// <summary>Whether to attempt revocation after local removal has committed.</summary>
    public bool RevokeRefreshToken { get; }
}
