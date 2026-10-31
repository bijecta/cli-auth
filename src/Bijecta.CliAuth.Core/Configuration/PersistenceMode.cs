namespace Bijecta.CliAuth.Core;

/// <summary>The persistence policy required by a profile; it never selects a backend.</summary>
public enum PersistenceMode
{
    /// <summary>Require an explicitly registered protected store.</summary>
    Protected,
    /// <summary>Keep credentials only in the current process.</summary>
    MemoryOnly,
    /// <summary>Persist unencrypted credentials in an explicitly selected local store; never Protected.</summary>
    /// <remarks>Part of session-key derivation: never rename or reorder this value.</remarks>
    Plaintext
}
