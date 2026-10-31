# 0008: Plaintext file persistence: integration, listing, and protections

Status: Accepted

Date: 2026-10-31 (integration and listing: storage-plan step 4; protections and
recovery: step 5)

## Context

ADR 0005 chose explicitly selected plaintext file persistence as the first
persistent backend. Storage-plan steps 1–3 implemented the file format, atomic
writes, matching cross-process leases (ADR 0006), and logout by record removal
(ADR 0007). `FileTokenStore` still could not be passed to `CliAuthenticator`,
because Core's `PersistenceMode` had no plaintext value and the store did not
implement `ITokenStore`. It also trusted whatever directory the host supplied.

With persistence, a host also needs to show which of its profiles currently have
a session, for example in a `status`/`list` command, without failing everything
when one profile's record is unreadable.

Plaintext files cannot protect against programs running as the same user; any
plaintext CLI shares that limit, and protected storage remains the stronger
future option. The protections below target other local users and mistakes:

- another user reading tokens from a readable directory or file;
- another user **planting** a session file. Session keys are deterministic, so
  anyone who knows the profile settings can compute the file name. In a shared
  directory such as `/tmp/myapp`, a planted valid-looking session would make the
  CLI silently use the attacker's account;
- misconfiguration, such as a world-writable or network-shared directory.

## Decision

### Plaintext persistence mode

Add `PersistenceMode.Plaintext`, appended after the existing values so their
numeric values do not change. The name describes the protection policy, not the
backend, like `Protected` and `MemoryOnly`. It is part of `SessionKey` derivation
(`Persistence.ToString()`), so it must not be renamed: a rename would change every
plaintext session's key. Plaintext is never reported as `Protected`.

`FileTokenStore` implements `ITokenStore` and declares `Plaintext` without I/O. It
already implements `ISessionLockProvider`, which `CliAuthenticator` uses as the
canonical lock domain (ADR 0004).

Selection stays explicit and two-sided: the profile requires `Plaintext` and the
host passes a store that declares it. Any mismatch is `InvalidConfiguration`;
there is no fallback. No extra acknowledgment flag or `IsPlaintext` property is
added: the mode itself is either `MemoryOnly`, `Protected`, or `Plaintext`.
`OidcProfile` keeps `Protected` as its default, so a host that does not choose a
policy fails at construction instead of writing plaintext.

The host supplies the absolute directory. No default-location helper is provided
yet; usage docs list conventional per-user locations. Warning users that tokens
are stored unencrypted is the host's presentation responsibility; Core reports the
policy through `LoginResult.Persistence` and prints nothing. Integration (step 4)
shipped before the protections (step 5) by maintainer decision.

### Profile session listing

Add `ICliAuthenticator.ListSessionsAsync`, returning one `ProfileSessionStatus`
per configured profile in configuration order. Each entry has the profile name
and either the same credential-free `SessionStatus` as `GetStatusAsync` or the
`AuthErrorCode` that prevented reading it (for example StorageCorrupt or
SessionBusy). One profile's failure does not fail the list. It performs no
discovery, refresh, or network I/O, acquires each profile's lease separately, and
propagates cancellation. Listing covers the authenticator's configured profiles;
session files are not enumerated, because file names are keyed hashes.

### Private storage locations

The store refuses locations that are not private to the current user, with the
new `AuthErrorCode.StorageInsecure` (appended to the enum). It never fixes
permissions silently: changing a host's directory is surprising, can mask an
attack, and a library cannot print warnings. Messages remain path-free. Checks run
on every lease acquisition, read, and write, because the directory can change.

**Directory:**

- A symlinked directory is allowed; its final target is validated instead, so a
  user's symlinked configuration directory keeps working. Parent directories are
  not checked (for example macOS `/tmp` → `/private/tmp`).
- **Unix:** the directory must not be writable by group or others. Readable by
  others (for example `0755`) is allowed: it reveals only hashed file names. New
  directories are created `0700`.
- **Windows:** new leaf directories get an explicit, protected (non-inherited)
  ACL granting full control only to the current user, SYSTEM, and Administrators.
  Existing directories must be owned by the current user (or Administrators,
  which elevated administrators produce) and must grant no read, write, delete,
  or permission rights, directly or by inheritance, to any other principal.
  `%LOCALAPPDATA%` passes.
- **Network drives** are refused: Windows UNC paths and `DriveType.Network`
  volumes, and Unix mounts that .NET's `DriveInfo` classifies as network, because
  ADR 0006's lock design needs native local semantics. Unknown or FUSE
  filesystems are not detected; if the mount table cannot be read, the check
  does not block.

**Files:**

- Session and lock files that are symlinks (Unix) or junctions/symbolic links
  (Windows) are refused.
- **Unix session files** must have no group or other permission bits, checked on
  the open handle. With the directory rule, "we could read it" implies "we own
  it" without native `stat` calls or per-OS struct layouts. Root can read any
  file and is outside this guarantee.
- **Windows session files** must pass the same ACL rule as the directory.
- Staging files are created exclusively (`CreateNew`), so they cannot be planted.

These managed checks run between path inspection and open. Given the directory
rule, only same-user processes can race them, and those can read the tokens
anyway. Race-free native opens (`O_NOFOLLOW`, `fstat`) and FIFO/special-file
detection are not added.

`FileTokenStore.DeleteAsync` skips the checks: removing our own record is always
safe, and deleting a symlinked record removes the link, never its target. Through
`CliAuthenticator`, logout works when only the session file is unsafe, because
the coordinator removes it without reading it. Logout still needs the session
lease, whose acquisition validates the directory, so an insecure directory must
be fixed (for example `chmod 700`) before any operation, including logout.
Skipping the lease would break ADR 0006's coordination.

### Corrupt records

A corrupt record never yields a token: status and acquisition return
StorageCorrupt and never repair it. Explicit login may replace it, since it is a
user action and the new session gets a fresh epoch. To stay race-safe, the
coordinator records that the record was corrupt when login started and, at
commit, replaces it only if it is **still** corrupt; if a valid session was
written meanwhile, login returns OperationSuperseded, and a record that became
corrupt during login is reported, not replaced. `UnsupportedSchema` (a newer
version's record) and `StorageInsecure` still block login; explicit logout removes
a newer-schema record (ADR 0007).

### Orphaned staging files

After every successful publication, the store deletes this key's orphaned
staging files, as logout already does (ADR 0007). Under the lease protocol no
cooperating writer is staging the same key. Orphans of keys never written again
remain (`0600` on Unix); no global or age-based sweep, because it could race
writers of other keys.

### Moving off plaintext later

Following the "move on next login" pattern of other CLIs, a future move from
`Plaintext` to `Protected` gives the profile a new key; the user logs in again.
The host (or a future helper) must delete the old plaintext record, which is
possible today with `SessionKey.ForProfile` on the plaintext profile and
`FileTokenStore.DeleteAsync`. A silent copy between backends, if wanted, belongs
to the protected-storage ADR and must respect ADR 0007's rule never to import a
session the user logged out of.

## Not decided here

ADR 0006 deferred lease- or epoch-validated mutation APIs. This decision does not
change the `ITokenStore` contract: `CliAuthenticator` is the supported path and
holds leases itself, while raw store callers remain caller-coordinated. Whether to
add enforced mutation overloads stays an open contract question for the
maintainer, to be revisited with refresh coordination.

## Composition layers

Core remains direct construction with no DI or console dependency. DI and
configuration binding belong to the Extensions package (implementation plan M5),
using a generic store registration so storage packages need no Extensions
dependency. Reusable CLI commands (login/logout/status/list) may later become a
separate package; console presentation stays out of Core. The sample CLI uses the
same public components as consumers and adds persistent `login`, `status`, `list`,
and `logout` commands with an explicit `--storage-dir`; it maps StorageInsecure to
the storage exit code with a fix-permissions hint.

## Consequences

Adding enum values and an interface member is a source-breaking change for code
that switches exhaustively over `PersistenceMode`/`AuthErrorCode` or implements
`ICliAuthenticator`; acceptable before 1.0. Existing directories that are group-
or world-writable, owned by another user, on a network share, or carrying broad
Windows ACLs now fail with StorageInsecure. Tests run the fake protocol against a
real `FileTokenStore` through the coordinator, including a second authenticator
instance standing in for the next CLI run, and write synthetic records with
owner-only access.

## Alternatives considered

- `PlaintextFile` or `Unprotected` as the mode name: ties the policy to one backend,
  or is less precise.
- An acknowledgment flag or a renamed `PlaintextFileTokenStore`: more friction
  without more safety than the existing two-sided match.
- A required `persistence` parameter: breaks every caller; the `Protected` default
  already fails closed.
- Listing by enumerating the storage directory: hashed names reveal no profiles,
  and it would expose other applications' sessions sharing the directory.
- Hardening before integration: safer on paper, but designed without real use
  through the authenticator.
- Fixing permissions automatically, or warning only: silently changes a
  host-owned directory, or has no presentation channel in a library.
- Native `lstat`/`fstat` ownership checks and `O_NOFOLLOW` opens: race-free and
  covers root, but needs per-OS, per-architecture struct layouts and flags.
- Checking parent directories (ssh `StrictModes` style): rejects common layouts
  and needs ownership data we avoid reading.
- A global orphan sweep: cannot know whether another key's writer is active.
