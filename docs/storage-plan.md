# File storage implementation plan

This is the canonical checklist and handoff for the file-storage milestone.
Update this file as part of the work, without waiting for the user to request it.
Architecture decisions belong in ADRs; this file records implementation status.

## Resume here

Last updated: 2026-10-31.

- Main includes steps 1–4: plaintext file persistence through CliAuthenticator
  (ADR 0008, merged from PR #2 as `338088a`), logout by record removal (ADR 0007),
  and cross-platform session leases (ADR 0006), each verified on the hosted
  Ubuntu/Windows/macOS matrix. Main is protected: changes land through PRs with all
  CI checks green. CI runs once per PR. Parent design documents stay outside Git.
- Step 5 decisions (ADR 0009, agreed with the maintainer 2026-10-31): refuse
  locations not private to the user with new `StorageInsecure` (Unix: no group/other
  write on the directory, no group/other bits on session files; Windows: owner-only
  ACLs, protected ACL on new directories); reject symlinked session/lock files and
  network drives; validate a symlinked directory through its target; explicit login
  replaces a still-corrupt record; clean this key's orphans after successful writes.
- Step 5 implementation on branch `feat/storage-filesystem-protections`.
- Verification checkpoint: Release build zero warnings/errors; 394 solution tests
  passed on macOS (4 Windows-only skipped); sample exercised against a world-writable
  directory (StorageInsecure, exit 5, fix-permissions hint).
- Next action: open the step 5 PR and confirm the hosted matrix, especially the
  Windows ACL tests and the Windows runner's temp-directory ACL; then merge with the
  maintainer's approval and plan step 6 (final process-race and package verification).
- Deferred limits: network drives are refused when DriveInfo reports them, but
  unknown/FUSE filesystems are not detected; FIFO/special-file and race-free
  native-open checks and root-user guarantees are out of scope (ADR 0009).
  Lease-required mutation APIs remain an open contract question (ADR 0008),
  revisited with refresh. Initial Linux import targets glibc.

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

### 4. Complete the storage contract and authentication integration — complete

- [x] Add an explicit plaintext persistence mode; never label this store Protected.
- [x] Make `FileTokenStore` implement the complete `ITokenStore` contract and expose
  its canonical matching `ISessionLockProvider` domain.
- [x] Integrate through existing Core injection without a Core dependency on Storage.File.
- [x] Test policy validation and coordinator behavior with persistent sessions.
- [x] Document explicit selection and host-owned directory configuration; no silent fallback.
- [x] Add `ListSessionsAsync` so hosts can show which profiles have sessions (ADR 0008).
- [x] Add persistent sample commands (`login`, `status`, `list`, `logout`).
- [x] Confirm the suite on the Ubuntu/Windows/macOS hosted matrix.

Evidence: `7e1ed51` (PR #2); hosted runs 41194606016 (push) and 41194609826 (pull
request) passed on Ubuntu, Windows, and macOS plus the Linux Keycloak job. [ADR 0008](decisions/0008-plaintext-persistence-integration.md).
Release build zero warnings/errors; 368 solution tests passed on macOS, including
coordinator tests that run the fake protocol against a real FileTokenStore (a new
authenticator per simulated CLI run), listing tests, and corrupt-record recovery.
Unit tests reference Storage.File; Core does not.

### 5. Filesystem protections and recovery — implementation in progress

- [x] Review and define the supported local-filesystem/path trust boundary (ADR 0009).
- [x] Verify restrictive permissions and define treatment of existing directories,
  Windows ACLs, ownership, symlinks, and path substitution.
- [x] Define safe, explicit corrupt-file recovery: explicit login replaces a record
  still corrupt at commit; newer-schema records still block login.
- [x] Define abandoned temporary-file handling: this key's orphans are removed after
  each successful write and on logout; no global sweep; lock identities untouched.
- [ ] Test supported protections and safe error reporting on all target platforms.

Evidence: In progress, hosted matrix pending. [ADR 0009](decisions/0009-file-store-protections.md).
New `AuthErrorCode.StorageInsecure`. Release build zero warnings/errors; 394 solution
tests passed on macOS with 4 Windows-only protection tests skipped there (they run on
the Windows runner). Through CliAuthenticator an insecure directory blocks every
operation, including logout, until fixed; file-level problems still allow logout.

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
