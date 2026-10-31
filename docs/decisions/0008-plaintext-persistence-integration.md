# 0008: Integrate plaintext file persistence and list profile sessions

Status: Accepted

Date: 2026-10-31

## Context

ADR 0005 chose explicitly selected plaintext file persistence as the first
persistent backend. Storage-plan steps 1–3 implemented the file format, atomic
writes, matching cross-process leases (ADR 0006), and logout by record removal
(ADR 0007). `FileTokenStore` still could not be passed to `CliAuthenticator`,
because Core's `PersistenceMode` had no plaintext value and the store did not
implement `ITokenStore`.

With persistence, a host also needs to show which of its profiles currently have
a session, for example in a `status`/`list` command, without failing everything
when one profile's record is unreadable.

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
policy through `LoginResult.Persistence` and prints nothing.

This integration precedes storage-plan step 5's filesystem hardening by maintainer
decision. Known gaps (existing-directory permissions, Windows ACL inheritance,
symlink and special-file rejection) remain documented until step 5.

### Profile session listing

Add `ICliAuthenticator.ListSessionsAsync`, returning one `ProfileSessionStatus`
per configured profile in configuration order. Each entry has the profile name
and either the same credential-free `SessionStatus` as `GetStatusAsync` or the
`AuthErrorCode` that prevented reading it (for example StorageCorrupt or
SessionBusy). One profile's failure does not fail the list. It performs no
discovery, refresh, or network I/O, acquires each profile's lease separately, and
propagates cancellation. Listing covers the authenticator's configured profiles;
session files are not enumerated, because file names are keyed hashes.

### Corrupt records

A corrupt record never yields a token: status and acquisition return
StorageCorrupt, and so does login, because it reads the current record first.
Recovery today is explicit logout (which removes the record without reading it,
ADR 0007) followed by login. Whether login itself may replace a corrupt record is
an open storage-plan step 5 question.

### Moving off plaintext later

Following the "move on next login" pattern of other CLIs, a future move from
`Plaintext` to `Protected` gives the profile a new key; the user logs in again.
The host (or a future helper) must delete the old plaintext record, which is
possible today with `SessionKey.ForProfile` on the plaintext profile and
`FileTokenStore.DeleteAsync`. A silent copy between backends, if wanted, belongs
to the protected-storage ADR and must respect ADR 0007's rule never to import a
session the user logged out of.

## Not decided here

ADR 0006 deferred lease- or epoch-validated mutation APIs to this step. This
integration does not change the `ITokenStore` contract: `CliAuthenticator` is the
supported path and holds leases itself, while raw store callers remain
caller-coordinated. Whether to add enforced mutation overloads stays an open
contract question for the maintainer, to be revisited with refresh coordination.

## Composition layers

Core remains direct construction with no DI or console dependency. DI and
configuration binding belong to the Extensions package (implementation plan M5),
using a generic store registration so storage packages need no Extensions
dependency. Reusable CLI commands (login/logout/status/list) may later become a
separate package; console presentation stays out of Core. The sample CLI uses the
same public components as consumers and adds persistent `login`, `status`, `list`,
and `logout` commands with an explicit `--storage-dir`.

## Consequences

Adding an enum value and an interface member is a source-breaking change for code
that switches exhaustively over `PersistenceMode` or implements
`ICliAuthenticator`; acceptable before 1.0. Tests run the fake protocol against a
real `FileTokenStore` through the coordinator, including a second authenticator
instance standing in for the next CLI run.

## Alternatives considered

- `PlaintextFile` or `Unprotected` as the mode name: ties the policy to one backend,
  or is less precise.
- An acknowledgment flag or a renamed `PlaintextFileTokenStore`: more friction
  without more safety than the existing two-sided match.
- A required `persistence` parameter: breaks every caller; the `Protected` default
  already fails closed.
- Listing by enumerating the storage directory: hashed names reveal no profiles,
  and it would expose other applications' sessions sharing the directory.
- Hardening (step 5) before integration: safer on paper, but designed without
  real use through the authenticator.
