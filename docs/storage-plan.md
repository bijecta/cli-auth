# File storage implementation plan

This is the canonical checklist and handoff for the file-storage milestone.
Update this file as part of the work, without waiting for the user to request it.
Architecture decisions belong in ADRs; this file records implementation status.

## Resume here

Last updated: 2026-10-31.

- Main includes steps 2 and 3: cross-platform session leases (`bff76ee`) and logout
  by record removal (ADR 0007, merged from PR #1 as `c31dcbb`/`3ac9db9`), each with a
  green Ubuntu/Windows/macOS hosted matrix. Main is protected: changes land through
  PRs with all CI checks green. Parent design documents stay outside Git.
- Step 4 decisions (ADR 0008, agreed with the maintainer 2026-10-31): append
  `PersistenceMode.Plaintext` (never renamed: it is part of session keys); explicit
  two-sided selection with no acknowledgment flag or `IsPlaintext`; `Protected`
  stays the default; host-owned directory with documented locations and no helper;
  plaintext warnings are the host's job; step 4 precedes step 5 hardening; add
  `ListSessionsAsync` with per-profile errors; moving off plaintext deletes the old
  record by its key. DI/configuration (Extensions, M5) and a reusable commands
  package are later layers.
- Step 4 implementation on branch `feat/storage-plaintext-integration`:
  `FileTokenStore : ITokenStore`, `ProfileSessionStatus`, `ListSessionsAsync`,
  coordinator tests against a real FileTokenStore, and sample `login`/`status`/
  `list`/`logout` commands with `--storage-dir`.
- Verification checkpoint: Release build zero warnings/errors; 368 solution tests
  passed on macOS; sample status/list/logout exercised against a temp directory,
  including corrupt records (listed per profile, recovered by logout). Sample login
  against a real provider was not exercised locally.
- Next action: open the step 4 PR, confirm the hosted matrix, merge with the
  maintainer's approval, then discuss step 5 (filesystem protections, including
  whether login may replace a corrupt record).
- Deferred limits: network shares are unsupported; automatic filesystem detection,
  symlink/descriptor checks, and ownership hardening remain step 5. Lease-required
  mutation APIs remain an open contract question (ADR 0008), revisited with refresh. Initial Linux import targets glibc.

## Status rules

Use `[ ]` for unfinished work and `[x]` only when the acceptance criteria are met.
For an active step, add `In progress` to its evidence. Keep pending verification
explicit; a local test does not establish that the hosted OS matrix passed.
After every completed step, update its checkbox, evidence, and Resume here before
ending the session or committing that step. If interrupted, record the partial
state and exact next action. Never store credentials or raw token data here.

## Checklist

### 1. Compact JSON and atomic read/write

- [x] Keep serialization and disk implementation outside Core.
- [x] Accept a host-supplied absolute storage directory without constructor I/O.
- [x] Serialize complete sessions as compact, versioned JSON; bound and validate reads.
- [x] Publish a flushed same-directory temporary file without truncating the old file.
- [x] Preserve old readers during replacement, including Windows native publication.
- [x] Test cancellation/failure before publication and temporary-file ownership.

Evidence: `bdfa989`, `57fc4aa`, and `0ade559`. Release solution build passed with
zero warnings/errors; 61 storage and 114 unit tests passed locally. User confirmed
Windows atomic writes work. Full Ubuntu/macOS CI results were not supplied in that
follow-up. Atomic publication is not a universal power-loss durability guarantee.

### 2. Matching session locks — complete

- [x] Select and document a Windows/Linux/macOS lock primitive and its limitations.
- [x] Implement `ISessionLockProvider` in Storage.File, keeping contracts in Core.
- [x] Coordinate the same session across store instances and separate processes.
- [x] Keep different session keys independently usable.
- [x] Support cancellable, bounded waits; map timeout to `SessionBusy`.
- [x] Make lease disposal safe and release ownership after abrupt process exit.
- [x] Define a stable lock-file identity/lifecycle so cleanup cannot split ownership.
- [x] Verify contention, cancellation, timeout, independent keys, and process exit
  using real subprocesses on the Ubuntu/Windows/macOS matrix.

Evidence: `bff76ee`; hosted run 41135383924 passed on Ubuntu, Windows, and macOS
(an earlier run failed only a Windows test-cleanup race, since fixed). ADR 0006 is accepted. The provider
and process helper are implemented. Locked restore and Release build passed with
zero warnings/errors; all 344 solution tests passed on macOS, including 85 storage
tests (24 lock cases). Usage docs and parent architecture match this slice.
Full solution dotnet format verification and git diff --check passed. Review
follow-up hardened probe budgets, helper host/output resolution, and open-failure
classification coverage. Real-process lock tests passed natively on all three
hosted runners. Atomic writes alone do not prevent lost
updates: the caller/coordinator must hold the matching lease across compound work.
Do not add nested automatic acquisition to store methods.

### 3. Logout by record removal — complete

Revised by [ADR 0007](decisions/0007-logout-deletes-session-records.md): logout
deletes the record instead of writing a tombstone; per-session epochs fence stale work.

- [x] Record the decision in ADR 0007 and supersede ADR 0005's tombstone item.
- [x] Make Core's `DeleteAsync` contract and `MemoryTokenStore` remove the record.
- [x] Add `FileTokenStore.DeleteAsync` under the caller-held lock, without parsing the record.
- [x] Remove unreadable records only through explicit logout; reads never repair them.
- [x] Remove this key's orphaned staging files best effort after the record is gone.
- [x] Test removal, missing/repeated logout, unreadable records, orphan scoping,
  cancellation, sanitized failures, open readers, and login superseded by logout.
- [x] Confirm the logout suite on the Ubuntu/Windows/macOS hosted matrix.

Evidence: `c69b08a` (PR #1); hosted runs 41143565907 (push) and 41143591372
(pull request) passed on Ubuntu, Windows, and macOS, including deletion with an open
reader on Windows, plus the Linux Keycloak integration job. Release build passed with zero
warnings/errors; all 358 solution tests passed on macOS. Coordinator tests now
assert that logout supersedes a login started while signed in and that a login
started while signed out commits (the accepted ADR 0007 trade-off). Parent SPECS,
ARCHITECTURE, and IMPLEMENTATION_PLAN were updated to match.

### 4. Complete the storage contract and authentication integration — implementation in progress

- [x] Add an explicit plaintext persistence mode; never label this store Protected.
- [x] Make `FileTokenStore` implement the complete `ITokenStore` contract and expose
  its canonical matching `ISessionLockProvider` domain.
- [x] Integrate through existing Core injection without a Core dependency on Storage.File.
- [x] Test policy validation and coordinator behavior with persistent sessions.
- [x] Document explicit selection and host-owned directory configuration; no silent fallback.
- [x] Add `ListSessionsAsync` so hosts can show which profiles have sessions (ADR 0008).
- [x] Add persistent sample commands (`login`, `status`, `list`, `logout`).
- [ ] Confirm the suite on the Ubuntu/Windows/macOS hosted matrix.

Evidence: In progress, hosted matrix pending. [ADR 0008](decisions/0008-plaintext-persistence-integration.md).
Release build zero warnings/errors; 368 solution tests passed on macOS, including
coordinator tests that run the fake protocol against a real FileTokenStore (a new
authenticator per simulated CLI run), listing tests, and corrupt-record recovery.
Unit tests reference Storage.File; Core does not.

### 5. Filesystem protections and recovery

- [ ] Review and define the supported local-filesystem/path trust boundary.
- [ ] Verify restrictive permissions and define treatment of existing directories,
  Windows ACLs, ownership, symlinks, and path substitution.
- [ ] Define safe, explicit corrupt-file recovery/reset behavior, including whether
  explicit login may replace a corrupt record instead of requiring logout first.
- [ ] Define abandoned temporary-file handling without racing an active writer or
  deleting lock identities. Document any intentionally deferred automatic cleanup.
- [ ] Test supported protections and safe error reporting on all target platforms.

Evidence: partial foundations only: new Unix directories/files request 0700/0600,
Windows inherits directory ACLs, reads are bounded, errors are sanitized, and readers
ignore temporary files. Full ownership/path hardening and orphan recovery remain open.

### 6. Final process-race and package verification

- [ ] Verify cross-process visibility and competing session updates.
- [ ] Verify login versus logout and stale refresh/login publication through Core.
- [ ] Verify abrupt termination during locking and before atomic publication.
- [ ] Confirm the complete storage suite passes on Ubuntu, Windows, and macOS.
- [ ] Verify a clean consumer can use the packaged file store with explicit selection.
- [ ] Update usage docs and implementation claims to match verified behavior.

Evidence: not started. Encryption/native secret stores, key rotation, optional
biometrics, and universal crash/power-loss guarantees are outside this milestone.

## References

- [Plaintext-first decision](decisions/0005-plaintext-persistence-first.md)
- [Accepted session-lock design](decisions/0006-cross-platform-session-locks.md)
- [Storage and profile scope](decisions/0004-authenticator-storage-and-profile-scope.md)
- [Session file format](usage/session-file-format.md)
- [File-store implementation notes](../src/Bijecta.CliAuth.Storage.File/README.md)

Parent workspace documents provide broader context when available. They are not
required build inputs and must not be copied into the repository.
