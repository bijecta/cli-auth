namespace Bijecta.CliAuth.Core;

/// <summary>Atomic envelope access for explicitly selected storage implementations.</summary>
/// <remarks>Compound operations require the matching session lock. Never silently replace corrupt data.</remarks>
public interface ITokenStore
{
    /// <summary>The backend's immutable declared persistence policy; this getter performs no I/O.</summary>
    PersistenceMode PersistenceMode { get; }

    /// <summary>Reads an immutable envelope, a credential-free tombstone, or null when never stored.</summary>
    ValueTask<StoredSession?> ReadAsync(SessionKey key, CancellationToken ct);
    /// <summary>Atomically replaces one envelope while the caller holds the matching lock.</summary>
    ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct);
    /// <summary>Removes credentials and advances retained epoch/generation metadata, even for a missing key.</summary>
    ValueTask DeleteAsync(SessionKey key, CancellationToken ct);
}
