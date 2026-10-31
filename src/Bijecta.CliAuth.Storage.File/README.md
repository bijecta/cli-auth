# File storage: reads, staged replacement writes, logout, and matching leases

`FileTokenStore` provides plaintext session-file reads, same-directory staged
replacement writes, and record removal for logout, plus matching local session
locks through `ISessionLockProvider`.
It references Core; Core does not reference it. `ITokenStore`,
`ISessionLockProvider`, and `MemoryTokenStore` remain in Core.

```csharp
var store = new FileTokenStore(storageDirectory);
await using var lease = await store.AcquireAsync(sessionKey, TimeSpan.FromSeconds(30), cancellationToken);
await store.WriteAsync(sessionKey, session, cancellationToken);
var restored = await store.ReadAsync(sessionKey, cancellationToken);
await store.DeleteAsync(sessionKey, cancellationToken);
```

The host supplies an absolute directory. Construction validates/normalizes the path
without filesystem access. Reads return null for a missing file/directory and do
not create anything. The host owns configuration/environment/default-directory
selection. Each session is stored as compact UTF-8 JSON in `<sessionKey>.json`.

## Write sequence

1. Serialize and validate before creating files or directories.
2. Create the directory on demand and a randomly named `.tmp` file inside it with
   CreateNew. Only this attempt's successfully created temporary file is owned.
3. Write all bytes, call Flush(flushToDisk: true), and close the temporary handle.
4. Check cancellation, then publish with Unix File.Move(overwrite: true) or Windows
   SetFileInformationByHandle(FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS).
5. Treat successful publication as committed; do not report late cancellation as
   though it undid the write. On failure/cancellation, best-effort remove only the
   owned temporary file without masking the original error.

The old destination is never truncated or deleted first. Readers allow deletion/
replacement sharing; Windows publication explicitly requests POSIX rename semantics
to replace a destination while its old read handles remain open.
Existing open readers retain the old file; subsequent readers open the replacement.
The temporary file is in the same directory to keep publication on one filesystem.
Windows uses the native rename API without a copy/delete or legacy replacement
fallback. It requires a Windows filesystem supporting FileRenameInfoEx with POSIX
replacement; unsupported operations return StorageUnavailable. Unix File.Move may
copy/delete across volumes, so the same-directory/stable local-filesystem requirement
still applies. Network shares and arbitrary mount changes are outside this scope.
See [Windows rename semantics](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information),
[File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move)
and the [format/atomicity walkthrough](../../docs/usage/session-file-format.md).

`FileTokenStore` implements `ITokenStore` (declaring `PersistenceMode.Plaintext`)
and `ISessionLockProvider`. Pass it to `CliAuthenticator` with profiles requiring
Plaintext; the authenticator holds leases for you. Direct callers hold the matching lease
across the complete read/check/write operation to prevent lost updates.
An explicit write does not inspect previous content for corruption.

## Matching session leases

`AcquireAsync(key, timeout, ct)` coordinates the same key across store instances
and cooperating processes using `<sessionKey>.lock` in the host-selected directory.
Different keys/directories remain independent. Construction still performs no I/O;
acquisition creates directories/files on demand. Read/write methods do not acquire
leases automatically. Callers must hold the lease across compound operations;
nested acquisition for the same key is not reentrant and eventually times out.

Windows keeps a FileShare.None handle open. Unix uses FileShare.None plus an
explicit nonblocking native flock on the same SafeFileHandle, so disabled or
best-effort .NET locking cannot silently grant a lease. Initial Linux support
targets glibc; musl is not verified. There is no extra in-process semaphore.
See [ADR 0006](../../docs/decisions/0006-cross-platform-session-locks.md).

The timeout must be positive and at most int.MaxValue milliseconds; invalid
timeouts return InvalidRequest. Waits use a monotonic budget and cancellable async
polling at 20–30 ms, capped by remaining time. Contention reaching the deadline
returns SessionBusy; cancellation throws OperationCanceledException. Other I/O,
permission, and native-load failures return sanitized StorageUnavailable.
Synchronous filesystem calls cannot be interrupted mid-call. No FIFO fairness or
starvation prevention is promised. On Windows, antivirus/indexer/sync-client
handles can cause transient contention too.

Disposal is idempotent and closes the owned handle once. Process termination also
releases ownership. Lock files are retained, never deleted, truncated, or replaced
by the provider. Their presence/content does not indicate ownership. Do not clean
them up while processes could reference them: doing so can split lock identity.

Only stable local filesystems with native lock semantics are supported. Unix
locking is advisory; arbitrary writers can bypass it. Network filesystems are
refused when .NET's DriveInfo reports them (ADR 0009); unknown or FUSE filesystems
are not detected. Emulated locks with process-owned semantics can fail to exclude
independent opens in one process.

File flushing does not make the subsequent directory rename fully power-loss
durable. No parent-directory synchronization or universal filesystem guarantee is
provided. Process termination can leave a private orphaned temporary file; readers
ignore it. The next successful write or logout of the same key removes it; orphans
of keys never written again remain, with no global sweep.

New Unix directories/staged files request owner-only modes (0700/0600); replacement
uses the staged file's mode. Files are unencrypted.

The store refuses locations that are not private to the current user, with
StorageInsecure ([ADR 0009](../../docs/decisions/0009-file-store-protections.md)):
- Unix: a storage directory writable by group or others, or a session file with
  any group/other permission bits. A directory readable by others (`0755`) is allowed.
- Windows: a directory or session file not owned by the current user (or
  Administrators) or granting read, write, delete, or permission rights to anyone
  else. New directories get a protected ACL for the user, SYSTEM, and Administrators.
- Session and lock files that are symlinks or junctions, and network drives
  (UNC paths and `DriveType.Network` mounts). A symlinked storage directory is
  checked through its target.

Checks run on every lease acquisition, read, and write. They cannot protect
against programs running as the same user, and root can read any file.
Reads are bounded to 1 MiB. Parser errors remain safe StorageCorrupt/UnsupportedSchema,
and filesystem failures map to StorageUnavailable without raw paths/inner exceptions.

## Cross-platform verification

The normal solution includes StorageTests, so the existing Ubuntu/Windows/macOS
build-and-test matrix runs the same atomicity tests on all three hosted runners. The initial Windows run found that legacy
MoveFileEx replacement failed with an open reader; the native POSIX-rename fix
was confirmed working by the maintainer. The lock suite also passes on all
three hosted runners.
Tests cover staging visibility, cancellation/failure preservation, cleanup ownership,
replacement with an open reader, and restrictive Unix replacement modes. An internal
pre-publication test boundary supports deterministic fault injection without adding
public hooks. Session-lock tests additionally use a disposable subprocess helper,
stdout/stdin readiness/release handshakes, controlled time, killed holders,
concurrent disposal, and disabled-implicit-locking children in both directions.
Hosted success must be established by actual CI results, not inferred
from the local macOS run. These tests do not simulate physical power failure.

## Use with CliAuthenticator

```csharp
var profile = new OidcProfile("example.my-cli", "work", issuer, clientId, scopes,
    persistence: PersistenceMode.Plaintext);
var auth = new CliAuthenticator([profile], new FileTokenStore(storageDirectory));
var sessions = await auth.ListSessionsAsync(cancellationToken);
```

Selection is explicit on both sides; any persistence mismatch is InvalidConfiguration.
Tokens are unencrypted, so tell users. Use a per-user directory such as
`~/.local/state/<app>`, `~/Library/Application Support/<app>`, or
`%LOCALAPPDATA%\<app>`. The directory must be private to the user, or operations
fail with StorageInsecure. See
[ADR 0008](../../docs/decisions/0008-plaintext-persistence-integration.md) and
[ADR 0009](../../docs/decisions/0009-file-store-protections.md).

## Logout

`DeleteAsync` removes `<sessionKey>.json` under the caller-held lease, without
reading it, so explicit logout can remove corrupt or unsupported-schema records.
Missing records/directories succeed and create nothing; repeated logout is a no-op.
`DeleteAsync` skips location checks; through CliAuthenticator, an insecure
directory must be fixed first because acquiring the lease validates it.
Afterwards this key's orphaned staging files are deleted best effort; the lock file
is kept. A failure to remove the record returns sanitized StorageUnavailable.
Every committed session has a fresh epoch, which fences work that started from it.
See [ADR 0007](../../docs/decisions/0007-logout-deletes-session-records.md).

See [ADR 0005](../../docs/decisions/0005-plaintext-persistence-first.md).
