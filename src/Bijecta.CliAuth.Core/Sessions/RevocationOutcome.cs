namespace Bijecta.CliAuth.Core;

/// <summary>The remote revocation result, separate from local logout.</summary>
public enum RevocationOutcome
{
    /// <summary>Revocation was not requested.</summary>
    NotRequested,
    /// <summary>The provider accepted the revocation request.</summary>
    Succeeded,
    /// <summary>The session or provider cannot support the requested refresh-token revocation.</summary>
    Unsupported,
    /// <summary>The remote revocation attempt failed.</summary>
    Failed
}
