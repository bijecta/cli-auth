# Compact JSON session format

The first storage slice implements `StoredSession` to/from compact UTF-8 JSON in
`Bijecta.CliAuth.Storage.File`. Core owns the contracts and the in-memory store;
this package owns file-format details. `CliAuthenticator` will continue to accept
an `ITokenStore` supplied by the host.

The serializer is internal; the public `FileTokenStore` uses it for disk reads,
staged replacement writes, and record removal. `FileTokenStore` implements
`ITokenStore` with `PersistenceMode.Plaintext` and `ISessionLockProvider`, so a host
passes it to `CliAuthenticator` with Plaintext profiles
([ADR 0008](../decisions/0008-plaintext-persistence-integration.md)). The examples
below use the store directly; through the authenticator, leases are handled for you.

## Disk usage

```csharp
var store = new FileTokenStore(storageDirectory);
await using var lease = await store.AcquireAsync(sessionKey, TimeSpan.FromSeconds(30), cancellationToken);
await store.WriteAsync(sessionKey, session, cancellationToken);
var restored = await store.ReadAsync(sessionKey, cancellationToken);
await store.DeleteAsync(sessionKey, cancellationToken); // logout: ReadAsync now returns null
```

`storageDirectory` must be absolute and is chosen by the host application, which
may supply its per-user default or read a user override. Construction validates and
normalizes the path without I/O. Reads return null for a missing file/directory,
without creating it. The first write creates the directory and writes one
`<sessionKey>.json` file. A new store instance using that directory can read it.

Writes serialize/validate before changing the filesystem. A unique CreateNew
`.tmp` file is written in the storage directory, flushed with Flush(flushToDisk:
true), and closed. After the final cancellation check, the platform replacement
operation publishes it at the session path. The destination is not truncated/deleted first.
Failures before publication preserve the previous session; best-effort cleanup
removes only this attempt's temporary file. Successful publication is the commit
boundary, with no cancellation check that could misreport an already completed write.

This targets stable local directories and same-filesystem replacement. Unix uses
File.Move(overwrite: true), which calls rename on the same filesystem. Windows now
opens the staged file with DELETE access and calls SetFileInformationByHandle with
FileRenameInfoEx and REPLACE_IF_EXISTS | POSIX_SEMANTICS. This explicitly permits
old open handles to remain valid while subsequent opens see the replacement.
Reads retain FileShare.Read | FileShare.Delete; write sharing is not enabled.

The original Windows matrix run failed its open-reader test with the legacy
MoveFileEx publication path. That test is retained and now also covers a Unicode
directory. Windows requires filesystem support for the extended POSIX rename;
unsupported operations fail as StorageUnavailable, with no copy/delete, delete-first,
or legacy replacement fallback. The maintainer confirmed Windows atomic writes
work after the correction; the lock tests also pass on the hosted matrix.
See [Microsoft's rename flag contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information),
[SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle),
and [.NET's Unix implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs).

Hold the matching lease across each compound read/check/write operation; raw
store methods do not acquire leases automatically. Leases use retained per-key
`.lock` files, exclusive Windows handles, and explicit Unix flock; cancellation
interrupts waits and expired contention returns SessionBusy. The provider never
deletes lock files, including on release or process exit. Only cooperating
processes on supported stable local filesystems are covered; network shares,
musl, and full path hardening are not verified. See the
[lease usage and limits](../../src/Bijecta.CliAuth.Storage.File/README.md#matching-session-leases).

`DeleteAsync` removes `<sessionKey>.json` without reading it, so corrupt and
unsupported-schema records can be removed by explicit logout; reads never repair
them. A missing record or directory succeeds without creating anything. After the
record is gone, the store deletes this key's orphaned `.<sessionKey>.<32 hex>.tmp`
staging files on a best-effort basis; logout is committed even if one cannot be
removed. Lock files are retained. Removal is not secure erasure and does not
synchronize the parent directory ([ADR 0007](../decisions/0007-logout-deletes-session-records.md)).

Complete-file publication without a caller-held lease does not prevent lost updates between writers. It also does
not establish universal power-loss durability: parent-directory synchronization
and filesystem-specific recovery are outside this slice. A killed process or
failed cleanup can leave an orphaned private temporary file; reads ignore it, and
logout of that key removes it. No broader orphan policy is implemented.

Filesystem failures return StorageUnavailable without raw paths or inner exceptions.
An explicit write does not inspect previous content for corruption. New Unix
directories/staged files request owner-only modes (0700/0600), and replacement keeps
the staged file's mode.

The store refuses locations that are not private to the current user, with
StorageInsecure ([ADR 0008](../decisions/0008-plaintext-persistence-integration.md)):
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

## Contents

One JSON document represents one session key, with these fields:

| Field | Purpose |
|---|---|
| `schemaVersion` | Existing Core envelope schema, currently 1 |
| `sessionKey` | The existing 64-character lowercase isolation key; checked against the expected key on read |
| `generation` | Existing mutation counter |
| `epoch` | Existing login/logout identity |
| `state` | Existing lifecycle name, such as Active or None |
| `issuer`, `clientId`, `subject` | Identity binding for credential-bearing states |
| `accessToken` | Value, UTC expiry, granted scopes, Bearer token type, and an optional UTC `obtainedAtUtc` (when the token response arrived; it bounds early refresh to the token's lifetime, [ADR 0009](../decisions/0009-silent-refresh.md)). Records without it remain valid |
| `refreshToken` | Optional refresh credential; omitted when absent |

A compact example using disposable synthetic values:

```json
{"schemaVersion":1,"sessionKey":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","generation":7,"epoch":"7348885f-33f4-43b0-b636-4c40eb5dd4a2","state":"Active","issuer":"https://issuer.example.test/Realm/","clientId":"sample-cli","subject":"synthetic-subject","accessToken":{"value":"synthetic-access","expiresAtUtc":"2031-01-02T10:00:00+00:00","grantedScopes":["openid","profile"],"tokenType":"Bearer","obtainedAtUtc":"2031-01-02T09:00:00+00:00"},"refreshToken":"synthetic-refresh"}
```

There is no indentation, byte-order mark, or trailing newline in the serialized
bytes. The existing Core schema version, generation, epoch, and lifecycle fields
are preserved now; migration/version-negotiation machinery is not part of this step.
The layout is one `<sessionKey>.json` per session in the host-supplied absolute
storage directory; the backend derives filenames internally.

Credential-free states (None, ReauthenticationRequired, Uncertain) contain only the
first five metadata fields. Active and RefreshInProgress include credentials and
identity binding. Logout removes the file rather than writing a None record; a
None record from any version still reads as no session. Raw ID tokens, browser transaction data, device codes, and user
codes are never part of this file format.

## Serialization and validation

Core deliberately excludes token values from ordinary JSON serialization. The
file serializer writes the known fields explicitly, so those diagnostic safeguards
remain intact. Serialized bytes intentionally contain credentials; do not log or
include real session bytes in exceptions, snapshots, or shareable artifacts.

Documents are bounded to 1 MiB and JSON depth 8. The reader rejects malformed JSON,
duplicate or unexpected properties, missing required values, invalid lifecycle
combinations, non-UTC/ambiguous expiry or obtained time, an obtained time after expiry, and a mismatched session key. Core constructors
validate the reconstructed session and token. These checks provide consistency;
they do not authenticate a plaintext file or prove that its contents were not edited.

Unknown envelope versions return UnsupportedSchema. Invalid content returns a safe
StorageCorrupt error without raw parsing exceptions or credential-bearing details.
An oversized in-memory session cannot be serialized and returns InvalidRequest.
The serializer neither repairs nor rewrites corrupt data.

## Current verification

Storage tests cover full-session round trips, exact issuer/credential preservation,
compact output, credential-free states, diagnostic redaction, key binding, schema
rejection, invalid/duplicate fields, expiry handling, and size bounds. Serializer tests use in-memory bytes; file-store tests additionally use disposable
temporary directories with synthetic credentials. They cover creation, missing reads,
readback through another instance, replacement, isolation, cancellation, corrupt/large
files, safe I/O errors, and new Unix modes. Atomic-write tests additionally verify staging visibility, original-session preservation
on cancellation/failure, temporary-file cleanup ownership, and old/new reader handles.
Logout tests cover removal across instances, missing records, repeated logout,
unreadable records, this-key-only orphan cleanup, cancellation, sanitized failures,
and deletion with an open reader followed by an immediate new write.
Lock tests additionally cover same-key contention, independent keys/directories,
bounded waits, cancellation, process termination, lease disposal races, and disabled
implicit .NET locking in subprocesses. Controlled-time tests verify deadlines and
retry behavior. Protection tests cover group/world-writable directories, session
files with broad modes, symlinked session/lock files and directories, Windows ACLs,
network-mount detection, and orphan cleanup after writes. They run in the existing
Ubuntu/Windows/macOS hosted matrix. Full power-loss durability and encryption remain
outside these tests.
