namespace Bijecta.CliAuth.Core;

/// <summary>The persistence policy required by a profile; it never selects a backend.</summary>
public enum PersistenceMode
{
    /// <summary>Require an explicitly registered protected store.</summary>
    Protected,
    /// <summary>Keep credentials only in the current process.</summary>
    MemoryOnly
}
