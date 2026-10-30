using System.Diagnostics;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Storage.File;

/// <summary>Plaintext session-file reads, staged replacement writes, and matching local session leases.</summary>
/// <remarks>
/// This incremental implementation does not yet implement ITokenStore: durable logout and persistence
/// policy integration remain later steps. Callers hold a matching lease across compound operations.
/// Replacement and locking target supported stable local filesystems, without full power-loss durability.
/// </remarks>
[DebuggerDisplay("FileTokenStore (plaintext, read/write only)")]
public sealed class FileTokenStore : ISessionLockProvider
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Func<string, CancellationToken, Task>? _beforePublish;
    private readonly FileSessionLockProvider _locks;

    /// <summary>Validates an explicitly supplied absolute directory without accessing the filesystem.</summary>
    public FileTokenStore(string storageDirectory) : this(storageDirectory, null)
    {
    }

    // Internal test seams for publication, lock timing, and the ownership handoff boundary.
    internal FileTokenStore(string storageDirectory, Func<string, CancellationToken, Task>? beforePublish,
        TimeProvider? timeProvider = null, Func<TimeSpan>? retryDelay = null, Action? beforeLockHandoff = null)
    {
        _beforePublish = beforePublish;
        if (string.IsNullOrWhiteSpace(storageDirectory) || !Path.IsPathFullyQualified(storageDirectory))
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        try { StorageDirectory = Path.GetFullPath(storageDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliAuthException(AuthErrorCode.InvalidConfiguration);
        }
        _locks = new FileSessionLockProvider(StorageDirectory, timeProvider ?? TimeProvider.System,
            retryDelay ?? (() => TimeSpan.FromMilliseconds(Random.Shared.Next(20, 31))), beforeLockHandoff);
    }

    /// <summary>Acquires the matching per-session lease across instances and cooperating local processes.</summary>
    /// <remarks>Hold this lease across compound reads and mutations. Lock files are retained after release.</remarks>
    public ValueTask<IAsyncDisposable> AcquireAsync(SessionKey key, TimeSpan timeout, CancellationToken ct) =>
        _locks.AcquireAsync(key, timeout, ct);

    /// <summary>The host-selected directory; individual file names are derived from session keys.</summary>
    public string StorageDirectory { get; }

    /// <summary>Reads one session, or returns null for a missing directory/file. Never creates directories.</summary>
    public async ValueTask<StoredSession?> ReadAsync(SessionKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            await using var stream = new FileStream(SessionPath(key), new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length > SessionFileSerializer.MaxDocumentBytes)
            {
                throw new CliAuthException(AuthErrorCode.StorageCorrupt);
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > SessionFileSerializer.MaxDocumentBytes)
                {
                    throw new CliAuthException(AuthErrorCode.StorageCorrupt);
                }
                buffer.Write(chunk, 0, count);
            }
            ct.ThrowIfCancellationRequested();
            return SessionFileSerializer.Deserialize(key, buffer.ToArray());
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliAuthException(AuthErrorCode.StorageUnavailable);
        }
    }

    /// <summary>Stages and flushes one complete compact JSON file, then replaces its destination.</summary>
    /// <remarks>Cancellation is observed before publication. A successful replacement is the commit boundary.</remarks>
    public async ValueTask WriteAsync(SessionKey key, StoredSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);
        // Validate/serialize before touching the existing file or creating a directory.
        var bytes = SessionFileSerializer.Serialize(key, session);
        ct.ThrowIfCancellationRequested();
        string? temporaryPath = null;
        var ownsTemporary = false;
        var published = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(StorageDirectory);
            }
            else
            {
                Directory.CreateDirectory(StorageDirectory, DirectoryMode);
            }
            temporaryPath = Path.Combine(StorageDirectory, $".{key.Value}.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            // The staged file becomes the destination, retaining its restrictive Unix creation mode.
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = FileModePermissions; }
            await using (var stream = new FileStream(temporaryPath, options))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                // FlushAsync alone does not request a disk flush. Flushing this size-limited payload is synchronous.
                stream.Flush(flushToDisk: true);
            }
            // Close the handle before publication, including on Windows.
            if (_beforePublish is not null) { await _beforePublish(temporaryPath, ct).ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                WindowsFilePublication.Publish(temporaryPath, SessionPath(key));
            }
            else
            {
                System.IO.File.Move(temporaryPath, SessionPath(key), overwrite: true);
            }
            published = true;
            // Do not report cancellation after a successful publication as though no commit occurred.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliAuthException(AuthErrorCode.StorageUnavailable);
        }
        finally
        {
            if (ownsTemporary && !published)
            {
                // Only delete the file this attempt created; never mask the original operation failure.
                try { System.IO.File.Delete(temporaryPath!); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string SessionPath(SessionKey key) => Path.Combine(StorageDirectory, key.Value + ".json");

    /// <summary>Formats without exposing the storage path or session contents.</summary>
    public override string ToString() => "FileTokenStore (plaintext, read/write only)";
}
