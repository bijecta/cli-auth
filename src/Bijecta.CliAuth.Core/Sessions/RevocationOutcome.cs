namespace Bijecta.CliAuth.Core;

/// <summary>The remote revocation result, separate from local logout.</summary>
public enum RevocationOutcome
{
    /// <summary>Revocation was not requested.</summary>
    NotRequested,
    /// <summary>The provider accepted the revocation request.</summary>
    Succeeded,
    /// <summary>The provider does not expose the requested revocation capability.</summary>
    Unsupported,
    /// <summary>The remote revocation attempt failed.</summary>
    Failed
}
