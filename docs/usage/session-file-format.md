# Compact JSON session format

The first storage slice implements `StoredSession` to/from compact UTF-8 JSON in
`Bijecta.CliAuth.Storage.File`. Core owns the contracts and the in-memory store;
this package owns file-format details. `CliAuthenticator` will continue to accept
an `ITokenStore` supplied by the host.

The serializer is internal. No public file store or file I/O is implemented in this
step, so it cannot yet be injected into the authenticator. Atomic replacement,
filesystem permissions/path handling, process locks, and durable logout operations
follow separately. The existing Core storage interface already requires atomic
access and matching coordination; this slice does not weaken that contract.

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
The planned layout is one `<sessionKey>.json` per session in a host-supplied storage
directory. Directory selection and actual path handling are not implemented yet.

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
rejection, invalid/duplicate fields, expiry handling, and size bounds. They exercise
serialization in memory using synthetic credentials. They do not establish file
permissions, durable writes, process safety, encryption, or a usable persistent store.
