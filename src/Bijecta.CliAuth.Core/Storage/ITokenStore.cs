namespace Bijecta.CliAuth.Core;

/// <summary>Atomic envelope access for explicitly selected storage implementations.</summary>
/// <remarks>Compound operations require the matching session lock. Never silently replace corrupt data.</remarks>
public interface ITokenStore
{
    /// <summary>The backend's immutable declared persistence policy; this getter performs no I/O.</summary>
    PersistenceMode PersistenceMode { get; }

    /// <summary>Reads an immutable envelope or credential-free lifecycle marker, or null when no record exists.</summary>
    ValueTask<StoredSession?> ReadAsync(SessionKey key, CancellationToken ct);
    /// <summary>Atomically replaces one envelope while the caller holds the matching lock.</summary>
    ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct);
    /// <summary>Removes the record while the caller holds the matching lock; a missing record is a successful no-op.</summary>
    /// <remarks>Removes unreadable records too. A failure must surface rather than report a successful removal.</remarks>
    ValueTask DeleteAsync(SessionKey key, CancellationToken ct);
}
