using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Storage.File;

internal sealed class FileSessionLockProvider(string directory, TimeProvider timeProvider,
    Func<TimeSpan> retryDelay, Action? beforeHandoff)
{
    internal async ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new CliAuthException(AuthErrorCode.InvalidRequest);
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new CliAuthException(AuthErrorCode.StorageUnavailable);
        }
        var started = timeProvider.GetTimestamp();
        FileStream? candidate = null;
        try
        {
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
            else
            {
                Directory.CreateDirectory(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var path = Path.Combine(directory, key.Value + ".lock");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Remaining() <= TimeSpan.Zero) { throw new CliAuthException(AuthErrorCode.SessionBusy); }
                var acquired = false;
                try
                {
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        BufferSize = 0
                    };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }
                    candidate = new FileStream(path, options);
                    // EX -> EX on the same open description, never a shared-lock upgrade.
                    // Explicit success is required even if .NET disabled or ignored implicit locking.
                    acquired = OperatingSystem.IsWindows() || UnixSessionLock.TryAcquire(candidate.SafeFileHandle);
                }
                catch (IOException error) when (IsContention(error)) { }

                if (acquired)
                {
                    beforeHandoff?.Invoke();
                    ct.ThrowIfCancellationRequested();
                    if (Remaining() <= TimeSpan.Zero) { throw new CliAuthException(AuthErrorCode.SessionBusy); }
                    var lease = new Lease(candidate!);
                    candidate = null;
                    return lease;
                }
                candidate?.Dispose();
                candidate = null;
                ct.ThrowIfCancellationRequested();
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) { throw new CliAuthException(AuthErrorCode.SessionBusy); }
                var delay = retryDelay();
                await Task.Delay(delay < remaining ? delay : remaining, timeProvider, ct).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException)
        {
            throw new CliAuthException(AuthErrorCode.StorageUnavailable);
        }
        finally { candidate?.Dispose(); }

        TimeSpan Remaining() => timeout - timeProvider.GetElapsedTime(started);
    }

    internal static bool IsContention(IOException error) => OperatingSystem.IsWindows()
        ? error.HResult == unchecked((int)0x80070020)
        : error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11);

    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        private FileStream? _stream = stream;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
