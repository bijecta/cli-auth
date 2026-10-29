# Compact JSON session format

The first storage slice implements `StoredSession` to/from compact UTF-8 JSON in
`Bijecta.CliAuth.Storage.File`. Core owns the contracts and the in-memory store;
this package owns file-format details. `CliAuthenticator` will continue to accept
an `ITokenStore` supplied by the host.

The serializer is internal; the public `FileTokenStore` now uses it for disk reads and staged replacement writes. The class is intentionally not yet an `ITokenStore`, so
it cannot be injected into the authenticator. Matching locks,
and durable logout operations follow separately. The existing Core storage
contract remains unchanged.

## Disk usage

```csharp
var store = new FileTokenStore(storageDirectory);
await store.WriteAsync(sessionKey, session, cancellationToken);
var restored = await store.ReadAsync(sessionKey, cancellationToken);
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
or legacy replacement fallback. A hosted rerun is needed to validate the correction.
See [Microsoft's rename flag contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information),
[SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle),
and [.NET's Unix implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs).

There are no coordination locks or durable logout operations yet. Complete-file
publication does not prevent lost updates between competing writers. It also does
not establish universal power-loss durability: parent-directory synchronization
and filesystem-specific recovery are outside this slice. A killed process or
failed cleanup can leave an orphaned private temporary file; reads ignore it.
No automatic orphan promotion or deletion policy is implemented.

Filesystem failures return StorageUnavailable without raw paths or inner exceptions.
An explicit write does not inspect previous content for corruption. New Unix
directories/staged files request owner-only modes (0700/0600), and replacement keeps
the staged file's mode. Existing directory permissions are unchanged, Windows ACLs
are inherited, and ownership/symlink/custom-ACL hardening remains later work.
The Core persistence enum/default and sample behavior are unchanged; the standalone
class is not yet injectable into CliAuthenticator.

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
| `accessToken` | Value, UTC expiry, granted scopes, and Bearer token type |
| `refreshToken` | Optional refresh credential; omitted when absent |

A compact example using disposable synthetic values:

```json
{"schemaVersion":1,"sessionKey":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","generation":7,"epoch":"7348885f-33f4-43b0-b636-4c40eb5dd4a2","state":"Active","issuer":"https://issuer.example.test/Realm/","clientId":"sample-cli","subject":"synthetic-subject","accessToken":{"value":"synthetic-access","expiresAtUtc":"2031-01-02T10:00:00+00:00","grantedScopes":["openid","profile"],"tokenType":"Bearer"},"refreshToken":"synthetic-refresh"}
```

There is no indentation, byte-order mark, or trailing newline in the serialized
bytes. The existing Core schema version, generation, epoch, and lifecycle fields
are preserved now; migration/version-negotiation machinery is not part of this step.
The layout is one `<sessionKey>.json` per session in the host-supplied absolute
storage directory; the backend derives filenames internally.

Credential-free states (None, ReauthenticationRequired, Uncertain) contain only the
first five metadata fields. Active and RefreshInProgress include credentials and
identity binding. This preserves the current model without implementing refresh or
logout persistence. Raw ID tokens, browser transaction data, device codes, and user
codes are never part of this file format.

## Serialization and validation

Core deliberately excludes token values from ordinary JSON serialization. The
file serializer writes the known fields explicitly, so those diagnostic safeguards
remain intact. Serialized bytes intentionally contain credentials; do not log or
include real session bytes in exceptions, snapshots, or shareable artifacts.

Documents are bounded to 1 MiB and JSON depth 8. The reader rejects malformed JSON,
duplicate or unexpected properties, missing required values, invalid lifecycle
combinations, non-UTC/ambiguous expiry, and a mismatched session key. Core constructors
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
They run in the existing Ubuntu/Windows/macOS matrix. Full power-loss durability,
coordination, permission/path hardening, and encryption remain outside these tests.
