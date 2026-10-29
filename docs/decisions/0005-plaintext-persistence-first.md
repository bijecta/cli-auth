# 0005: Implement explicit plaintext persistence before protected backends

Status: Accepted

Date: 2026-10-29

## Context

Browser and device login now work with explicitly selected process-local memory
storage. A session still disappears when the CLI exits. The next milestone needs
to establish reliable persistence and coordination between CLI processes.

The original design required protected persistence before a usable preview and
specified DPAPI on Windows plus OS-held AES keys on macOS/Linux. After discussing
direct credential-store storage and encrypted files, the maintainer chose to
implement plaintext file persistence first. This is a deliberate scope and policy
change: it avoids blocking session coordination on native integrations and key
management, and accepts readable credentials in the selected local files.

Existing CLI tools provide precedent for plaintext storage as a product choice:
[Codex documents file, keyring, automatic, and ephemeral modes](https://learn.chatgpt.com/docs/auth#credential-storage),
and [GitHub CLI documents a plaintext fallback](https://cli.github.com/manual/gh_auth_login).
Those policies do not establish the safety of plaintext or prescribe our fallback
behavior. We retain explicit backend selection.

## Decision

Implement an explicitly selected plaintext file backend as the first persistent
backend. Keep memory-only storage available. A host must deliberately choose the
plaintext backend and matching persistence policy; do not silently downgrade a
protected backend, automatically detect a backend, or change an existing profile's
policy. Plaintext must never be reported as `Protected`.

Prioritize the following behavior for the first persistence milestone:

- A versioned session envelope, including access/refresh credentials, identity
  binding, expiry, epoch, and generation; no raw ID-token persistence.
- Current-user local storage, restrictive directory/file permissions, safe path
  handling, and rejection of unsafe file substitutions where supported.
- Complete-envelope atomic replacement with private temporary files and documented
  flush/crash behavior; failed commits must not be reported as successful.
- Matching process-local and cross-process locks with bounded, cancellable waits,
  stable lock files, and no forced takeover of a live owner.
- Durable credential-free logout tombstones so stale login or later refresh work
  cannot resurrect a logged-out session.
- Explicit corruption/schema failures, independent profile progress, and real
  multi-process tests including abrupt termination and competing login/logout.
- Credential-free diagnostics, logs, snapshots, and test artifacts.

File permissions restrict ordinary filesystem access but do not encrypt the
session or authenticate its contents. Schema/binding checks detect malformed or
inconsistent records; they do not prove that a valid-looking plaintext record has
not been edited. Anyone who can read or copy the credential file can obtain its
tokens. Document these limits and keep real credentials out of repositories and
shareable artifacts. Tests may inspect only disposable synthetic credential files.

## Protected storage is a later, open decision

Defer encrypted files and direct native credential-store backends until a separate
ADR evaluates their benefits, constraints, dependencies, and operational behavior.
The earlier DPAPI/Keychain/Secret Service plus AES layout is a candidate, not an
accepted implementation mandate after this decision.

That later discussion must cover direct session storage versus encrypted envelopes,
key provisioning, storage, access control, rotation, loss/recovery, migration,
installation identity, and dependency/runtime support. PKI is not assumed to be
necessary for local encryption; any certificate or hardware-backed design needs
its own justification. No generic `encrypt: true` flag or encryption key format is
selected here. Backend composition through `ITokenStore` remains the foundation.

Platform user verification, such as Windows Hello or platform-supported biometrics,
is also deferred. It is an access policy around credential/key use, not itself a
storage format or encryption algorithm. The maintainer's facial-authentication
idea remains an exploration item; no particular Mac biometric mechanism, hardware,
or API is promised. Future work must verify actual platform capabilities and keep
silent acquisition/status free of unexpected authentication dialogs.

## Consequences and follow-up

The first usable preview may use explicitly selected plaintext persistence;
protected storage is no longer its prerequisite. Storage correctness, protocol
validation, race safety, and accurate security claims remain required. Encryption
would add at-rest protection later but would not replace coordination or protect
against every process running as the same user.

[ADR 0004](0004-authenticator-storage-and-profile-scope.md) still governs one store
and matching lock domain per authenticator. Package placement, the plaintext public
policy value, file layout, and the exact lock primitive need implementation design
before coding; this ADR does not create APIs or choose native dependencies. Existing
platform projects remain scaffolds and do not imply supported protected backends.

Update the architecture, specifications, milestone plan, and sample guidance to
reflect this decision. Implementation and its tests are the next work item. The
current code still exposes only `Protected` and `MemoryOnly` persistence policies
and implements only memory storage; this record does not claim plaintext storage
already works.

## Alternatives considered

- Implement all protected backends first: retains the original protection policy
  but delays persistence while native dependencies and key lifecycle are unresolved.
- Store complete sessions directly in OS credential stores first: may reduce custom
  encryption code but still requires native integration, size/atomicity evaluation,
  coordination, and noninteractive-access decisions.
- Add one encryption boolean: leaves backend, key ownership, availability, migration,
  and prompting semantics unclear.
- Automatically fall back to plaintext: changes the host's selected protection
  policy unexpectedly and conflicts with explicit store composition.

## First implementation slice

The maintainer selected compact JSON and step-by-step implementation.
`Bijecta.CliAuth.Storage.File` now owns the internal serializer described in the
[session format](../usage/session-file-format.md). The existing schema/epoch/generation
fields are retained; migrations, file I/O, atomic replacement, and process locking
are later slices. Core still contains the contracts and memory store. No public
file backend or plaintext persistence policy has been exposed yet.

## Basic disk read/write slice

The maintainer next requested `FileTokenStore(storageDirectory)` with only reads
and direct writes. The host supplies an absolute directory; construction performs
no I/O, missing reads create nothing, and writes create the directory on demand.
Each session uses `<sessionKey>.json`. Writes are explicitly non-atomic and may
leave partial data on interruption. No locking or durable logout is added in this
slice. New Unix files/directories request owner-only modes; existing permissions,
Windows ACL hardening, and symlink/ownership checks remain later work.

The standalone class does not yet implement ITokenStore, preserving that existing
contract's stronger atomicity/logout guarantees until the corresponding steps are
implemented. Core's persistence enum/default and authenticator integration are
unchanged. See [file usage and limits](../usage/session-file-format.md).

## Atomic replacement slice

After discussing OS replacement operations and atomic visibility versus durability,
the maintainer requested atomic writes only, with coordination deferred. FileTokenStore
now stages complete bytes in a unique CreateNew file beside the destination, flushes
the file to disk, closes it, and publishes with File.Move(overwrite: true). Readers
allow deletion/replacement sharing for Windows. Failed/cancelled attempts clean up
only their owned temporary files; a successful publication is the commit boundary.

This targets stable local same-filesystem replacement, not a universal guarantee
for every filesystem or physical power failure. Directory synchronization, orphan
recovery, locks, and durable logout remain deferred. The class is still separate
from the full ITokenStore contract. Cross-platform behavioral tests belong to the
normal solution and therefore run in the existing Ubuntu/Windows/macOS CI matrix.
Actual hosted passes must be checked after CI runs; local macOS is not evidence
that Windows/Linux behavior has passed.
