namespace Bijecta.CliAuth.Core;

/// <summary>Serializes session operations for a store's coordination domain.</summary>
public interface ISessionLockProvider
{
    /// <summary>Acquires an independently cancellable lease, or fails with SessionBusy on timeout.</summary>
    ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, TimeSpan timeout, CancellationToken ct);
}
