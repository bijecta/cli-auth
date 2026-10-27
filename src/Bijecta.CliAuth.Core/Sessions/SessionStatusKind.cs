namespace Bijecta.CliAuth.Core;

/// <summary>Local status only; it does not assert live provider or API validity.</summary>
public enum SessionStatusKind
{
    /// <summary>No local credentials are present.</summary>
    None,
    /// <summary>A local access token is outside the expiry margin.</summary>
    Available,
    /// <summary>A refresh token exists and acquisition must refresh.</summary>
    RefreshRequired,
    /// <summary>Explicit login is needed.</summary>
    ReauthenticationRequired,
    /// <summary>Refresh outcome is uncertain; credentials must not be reused.</summary>
    Uncertain
}
