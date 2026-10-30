namespace Bijecta.CliAuth.Core;

/// <summary>Persisted session lifecycle states, separate from time-dependent local status.</summary>
public enum SessionLifecycleState
{
    /// <summary>A credential-free marker meaning no session; logout removes records rather than writing this.</summary>
    None,
    /// <summary>A validated session whose token may be usable or require refresh.</summary>
    Active,
    /// <summary>A refresh owns the session; an orphaned marker must be treated as uncertain.</summary>
    RefreshInProgress,
    /// <summary>Credentials were cleared and explicit login is required.</summary>
    ReauthenticationRequired,
    /// <summary>Refresh may have rotated credentials; do not reuse them.</summary>
    Uncertain
}
