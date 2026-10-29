# File storage: reads and staged replacement writes

`FileTokenStore` provides plaintext session-file reads and same-directory staged
replacement writes. It references Core; Core does not reference it. `ITokenStore`,
`ISessionLockProvider`, and `MemoryTokenStore` remain in Core.

```csharp
var store = new FileTokenStore(storageDirectory);
await store.WriteAsync(sessionKey, session, cancellationToken);
var restored = await store.ReadAsync(sessionKey, cancellationToken);
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
4. Check cancellation, then call File.Move(temp, destination, overwrite: true).
5. Treat successful publication as committed; do not report late cancellation as
   though it undid the write. On failure/cancellation, best-effort remove only the
   owned temporary file without masking the original error.

The old destination is never truncated or deleted first. Readers allow deletion/
replacement sharing so Windows can replace a file with readers still holding it.
Existing open readers retain the old file; subsequent readers open the replacement.
The temporary file is in the same directory to keep publication on one filesystem.
.NET can copy/delete for cross-volume moves, so this design targets stable local
storage directories, not network shares or arbitrary filesystem/mount changes.
See [File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move)
and the [format/atomicity walkthrough](../../docs/usage/session-file-format.md).

This incremental class still does not implement ITokenStore: matching locks,
durable logout, and explicit plaintext-policy integration remain later steps.
Atomic publication does not prevent two writers from losing each other's updates.
An explicit write does not inspect previous content for corruption.

File flushing does not make the subsequent directory rename fully power-loss
durable. No parent-directory synchronization or universal filesystem guarantee is
provided. Process termination can leave a private orphaned temporary file; readers
ignore it. Cleanup failures can also leave temporary files. Automatic orphan cleanup
and crash-recovery policy are not implemented.

New Unix directories/staged files request owner-only modes (0700/0600); replacement
uses the staged file's mode. Existing directory permissions are unchanged. Windows
files inherit directory ACLs; preserving custom destination ACLs and full ownership/
symlink/path-substitution hardening remain future work. Files are unencrypted.
Reads are bounded to 1 MiB. Parser errors remain safe StorageCorrupt/UnsupportedSchema,
and filesystem failures map to StorageUnavailable without raw paths/inner exceptions.

## Cross-platform verification

The normal solution includes StorageTests, so the existing Ubuntu/Windows/macOS
build-and-test matrix runs the same atomicity tests on all three hosted runners.
Tests cover staging visibility, cancellation/failure preservation, cleanup ownership,
replacement with an open reader, and restrictive Unix replacement modes. An internal
pre-publication test boundary supports deterministic fault injection without adding
public hooks. Hosted success must be established by actual CI results, not inferred
from the local macOS run. These tests do not simulate physical power failure.

See [ADR 0005](../../docs/decisions/0005-plaintext-persistence-first.md).
