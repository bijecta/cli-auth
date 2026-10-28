using System.Collections.Concurrent;

namespace Bijecta.CliAuth.Core;

/// <summary>Explicit process-local memory persistence and matching per-key session locks.</summary>
/// <remarks>Share this instance for coordinated access. It never persists across process exit.</remarks>
public sealed class MemoryTokenStore : ITokenStore, ISessionLockProvider
{
    private readonly ConcurrentDictionary<SessionKey, StoredSession> _sessions = new();
    private readonly ConcurrentDictionary<SessionKey, SemaphoreSlim> _locks = new();

    /// <inheritdoc />
    public PersistenceMode PersistenceMode => PersistenceMode.MemoryOnly;

    /// <inheritdoc />
    public ValueTask<StoredSession?> ReadAsync(SessionKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        _sessions.TryGetValue(key, out var value);
        return ValueTask.FromResult(value);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);
        _sessions[key] = session;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(SessionKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        _sessions.AddOrUpdate(key, _ => StoredSession.Removed(null), (_, previous) => StoredSession.Removed(previous));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        // Stable gates are retained for this store's lifetime; removing a live gate can split ownership.
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(timeout, ct).ConfigureAwait(false))
        {
            throw new CliAuthException(AuthErrorCode.SessionBusy);
        }
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
