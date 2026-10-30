# File storage implementation plan

This is the canonical checklist and handoff for the file-storage milestone.
Update this file as part of the work, without waiting for the user to request it.
Architecture decisions belong in ADRs; this file records implementation status.

## Resume here

Last updated: 2026-10-30.

- Current implementation commit: `bff76ee` (cross-platform session leases),
  fast-forward merged to main with the maintainer's approval.
- ADR 0006 is Accepted by the maintainer (2026-10-30). The implementation,
  review follow-up, and documentation are on main; no packages are published.
  Parent design documents stay outside Git.
- Windows atomic writes: user confirmed working after the correction.
- Step 2 implementation: FileTokenStore now implements ISessionLockProvider using
  retained per-key lock files, exclusive Windows handles, and Unix FileShare.None
  followed by explicit nonblocking flock on the same SafeFileHandle. Fixed per-OS
  imports avoid an assembly-wide resolver. There is no extra in-process gate.
- The provider uses one TimeProvider-based deadline, cancellable delays with jitter,
  exact contention error codes, and idempotent disposal. Store read/write methods
  remain caller-coordinated; durable logout and ITokenStore integration are later.
- Review follow-up (2026-10-30): test harness hardened after review. Probes
  expecting ACQUIRED now wait 5 s (SessionBusy probes stay at 150 ms) to absorb
  transient Windows antivirus/indexer contention; the helper starts through
  DOTNET_HOST_PATH when set; the helper is copied from its resolved project output
  (OutputItemType) including subfolders; a new test proves a non-contention open
  failure (lock path is a directory) returns sanitized StorageUnavailable without retry.
- Verification checkpoint: Release solution build passed with zero warnings/errors.
  Full macOS solution tests passed: 344 total, no failures or skips (85 storage
  tests, including 24 lock cases); the storage suite also passed 4 repeated runs.
  A Debug build of the test project alone also copies the helper. Real subprocess
  tests cover disabled-implicit-locking in both directions, kill recovery,
  non-inheritance, and concurrent disposal; controlled-time tests cover
  retry/cancellation/handoff.
- Formatting/review: full solution `dotnet format --verify-no-changes --no-restore`
  (the CI command) and git diff --check passed locally.
- Hosted run 41133464541 (first push): ubuntu-latest and macos-latest passed;
  windows-latest failed 1 of 344 tests. SpawnedChildDoesNotInheritParentsUnrelatedLease
  left its child to be killed by disposal, and deleting the test directory then hit a
  sharing violation on the child's lock file: Windows can report a killed process
  as exited before its handles close. The provider was not at fault. Fix: the test
  now releases the child through the stdin protocol, and test-directory cleanup
  retries for up to ~2 s on IOException/UnauthorizedAccessException.
- Hosted run 41135383924 on `bff76ee`: ubuntu-latest, windows-latest, macos-latest,
  and the Linux Keycloak integration job all passed. Step 2 is complete.
- Next action: discuss step 3's durable logout/tombstone behavior with the
  maintainer before implementation. Merge to main only after the
  matrix passes and the maintainer approves.
- Deferred limits: network shares are unsupported; automatic filesystem detection,
  symlink/descriptor checks, and ownership hardening remain step 5. Lease-required
  mutation APIs remain a step-4 contract question. Initial Linux import targets glibc.

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

### 3. Durable logout and retained metadata

- [ ] Add `DeleteAsync` under the matching caller-held lock.
- [ ] Atomically publish a credential-free tombstone rather than unlinking history.
- [ ] Advance retained epoch/generation metadata, including for a missing session,
  consistently with the Core contract and `StoredSession.Removed` semantics.
- [ ] Preserve corruption/schema errors instead of silently overwriting bad data.
- [ ] Test repeated logout, missing sessions, reopen, and stale-login/refresh rejection.

Evidence: not started. The tombstone fences work that began before logout.

### 4. Complete the storage contract and authentication integration

- [ ] Add an explicit plaintext persistence mode; never label this store Protected.
- [ ] Make `FileTokenStore` implement the complete `ITokenStore` contract and expose
  its canonical matching `ISessionLockProvider` domain.
- [ ] Integrate through existing Core injection without a Core dependency on Storage.File.
- [ ] Test policy validation and coordinator behavior with persistent sessions.
- [ ] Document explicit selection and host-owned directory configuration; no silent fallback.

Evidence: not started. `FileTokenStore` currently exposes reads/writes and matching leases, and
deliberately does not implement `ITokenStore` yet.

### 5. Filesystem protections and recovery

- [ ] Review and define the supported local-filesystem/path trust boundary.
- [ ] Verify restrictive permissions and define treatment of existing directories,
  Windows ACLs, ownership, symlinks, and path substitution.
- [ ] Define safe, explicit corrupt-file recovery/reset behavior.
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
