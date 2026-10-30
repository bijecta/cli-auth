# 0006: Cross-platform session leases on stable lock files

Status: Accepted

Revision: Addresses the maintainer's review of the Unix open mechanism and
network-filesystem limits. Second revision replaces the assembly-wide import
resolver with per-OS imports and makes Windows/Unix error classification exact.
Accepted by the maintainer on 2026-10-30. Implementation is in progress; verification
status is maintained in docs/storage-plan.md.

Date: 2026-10-30

## Context

Atomic JSON replacement is implemented and the maintainer confirmed the Windows
correction works. Atomic publication alone cannot serialize compound session
operations. Independent instances and CLI processes must use the same lock domain,
with cancellation, bounded waiting, and release after process termination.

## Decision

Make `FileTokenStore` implement Core's existing `ISessionLockProvider` contract.
Keep the contract in Core and all disk/native code in Storage.File. Construction
continues to validate the host-supplied absolute directory without filesystem I/O.
The class still will not implement `ITokenStore` until durable logout and explicit
plaintext policy are complete.

For each `<sessionKey>.json`, use a separate persistent `<sessionKey>.lock` in the
same directory. The directory and validated lowercase session key define the
coordination domain. Acquisition creates the directory and lock file on demand;
new Unix directories/files request 0700/0600. The file has no credentials, PID,
timestamp, or ownership marker. Existing permissions and Windows ACL inheritance
retain their currently documented limitations.

Use these OS primitives:

- Windows: open/create the lock file for read/write with `FileShare.None`, keeping
  the exclusive, noninheritable handle alive for the lease. Retry only sharing
  contention: an `IOException` whose HResult is HRESULT_FROM_WIN32(32)
  (`ERROR_SHARING_VIOLATION`, `0x80070020`). Compare the whole HResult, never the
  message. Every other exception, including `UnauthorizedAccessException`, is a
  sanitized StorageUnavailable rather than contention.
  Antivirus, indexers, and sync clients such as OneDrive can cause this contention
  too; a short deadline can therefore produce an apparently spurious SessionBusy.
- Linux/macOS: open with the concrete managed options below, then explicitly call
  native `flock(LOCK_EX | LOCK_NB)` on that stream's **same** SafeFileHandle. Keep
  the stream alive for the lease. FileShare.None excludes FileShare.Inheritable,
  which requests O_CLOEXEC in the cited runtime implementation.

```csharp
var stream = new FileStream(lockPath, new FileStreamOptions
{
    Mode = FileMode.OpenOrCreate,
    Access = FileAccess.ReadWrite,
    Share = FileShare.None,
    BufferSize = 0,
    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
});
// Always required before handoff, even when the open succeeded:
// flock(stream.SafeFileHandle, LOCK_EX | LOCK_NB)
```

These options are the Unix branch; do not set UnixCreateMode on Windows. An
implicit exclusive lock followed by an explicit exclusive request on the same
open file description retains exclusive ownership, without a shared-lock upgrade.
If implicit locking is disabled or silently fails, the explicit call must acquire
the lock or surface its error. Dispose the stream on any unsuccessful attempt.

Contention has two entry points: IOException from the managed open, or errno from
the explicit flock. For the first, .NET 10 maps a would-block implicit lock to
`new IOException(message, errorInfo.RawErrno)`, so HResult holds the raw errno;
EACCES/EPERM surface as UnauthorizedAccessException and are StorageUnavailable. Classify EWOULDBLOCK by platform:
11 on Linux, 35 on macOS, never by message. Retry interrupted native calls within
the same deadline/cancellation budget. Unsupported locking, ENOLCK, and other
failures return sanitized StorageUnavailable rather than permission to proceed.

Do not rely solely on Unix `FileShare.None`: the .NET 10 implementation can disable
its advisory locks through configuration and ignores lock failures other than
would-block. An explicit native call must succeed before returning a lease.
Declare `flock(SafeFileHandle fd, int operation)` with LibraryImport and
SetLastError = true. SafeHandle input marshalling keeps the descriptor alive;
manual DangerousAddRef/Release is unnecessary. Read Marshal.GetLastPInvokeError
immediately after failure. Declare two OS-specific imports with fixed library
names: `libc.so.6` for glibc Linux and `/usr/lib/libSystem.B.dylib` for macOS,
chosen at runtime by OperatingSystem.IsLinux()/IsMacOS(); neither is called on
another OS. Do not rely on bare `libc` probing and do not register an assembly
DllImportResolver: a resolver can be set only once per assembly, a second
registration throws, and it intercepts every native load the assembly makes,
including the existing kernel32 imports used for Windows publication. Initial
Linux verification targets glibc; musl needs its own import declaration and
verification before claiming support. DllNotFoundException and
EntryPointNotFoundException map to StorageUnavailable.

On supported local filesystems with native flock semantics, independent opens
contend within one process as well as across processes. An
additional process-wide semaphore registry is unnecessary for correctness and is
not part of this slice. The parent ARCHITECTURE.md describes this same
single-OS-lease sequence, replacing its earlier thread-gate-plus-file-lock
arrangement. This avoids retaining a global entry for every directory/key.
MemoryTokenStore's process-local semaphore is unchanged. No FIFO fairness or
reentrant acquisition is promised.

This is not an assertion about every filesystem called through flock: Linux NFS
uses whole-file byte-range emulation and SMB/CIFS behavior also differs. On an
implementation with process-owned emulated locks, independent opens in one process
can fail to exclude each other, silently invalidating this design's no-semaphore
argument. Byte-range emulation alone does not prove that ownership model for every
Linux NFS client: kernel NFS code retains FL_FLOCK handling. Network filesystems
are unsupported regardless of their particular ownership behavior. This slice
documents the exclusion; it does not claim automatic filesystem-type rejection.
A descriptor-based statfs check rejecting known NFS/SMB/CIFS types is deferred to
step 5, with platform ABI tests; a denylist alone would not prove local semantics
for unknown/FUSE filesystems. The host must select a supported local directory.

## Waiting and disposal

Match `MemoryTokenStore`'s timeout validation: strictly positive, finite, and at
most `int.MaxValue` milliseconds. Invalid values return `InvalidRequest`.
Inject TimeProvider through an internal test seam, defaulting to TimeProvider.System.
Use GetTimestamp/GetElapsedTime for one monotonic budget covering the entire
acquisition, and Task.Delay(delay, timeProvider, ct) between nonblocking attempts.
Poll at 25 ms with small jitter (for example, +/-5 ms), capped by remaining time;
provide deterministic jitter in timing tests. Jitter reduces synchronized retries
but does not guarantee fairness: a holder that repeatedly releases and reacquires
can still starve a waiter until that waiter's deadline. Native calls cannot be
cancelled mid-call; this is a local-filesystem contract, not a hard real-time bound.

Observe cancellation before I/O, between attempts, and before handing ownership
to the caller. Release any acquired handle if cancellation/deadline wins before
handoff. Cancellation throws `OperationCanceledException`; expired contention
returns `SessionBusy`. Permission, path, unsupported-platform, and other storage
failures return `StorageUnavailable` without exposing paths or native messages.

Return an idempotent `IAsyncDisposable` lease. Disposal atomically takes and closes
its owned handle once. Closing releases the OS lock; normal OS process teardown
also closes the handle. Do not deliberately duplicate or inherit lease handles:
on Unix a surviving duplicate can retain ownership after the original process exits.

## Stable identity and integration

Never unlink, replace, truncate, or use DeleteOnClose for lock files. Leave them
after disposal, cancellation, timeout, logout, and abrupt termination. An empty
file's existence never means the lock is held. Unlinking a file still referenced
by an owner/waiter could let another process create a second lock identity.
No stale-PID detection, age-based takeover, or automatic lock-file cleanup.

The coordinator/caller holds the lease across reload, checks, and atomic commit.
Read/write/delete methods do not acquire it automatically; nested acquisition
would deadlock compound operations. Different keys use different files and can
progress independently. Raw read/write methods remain caller-coordinated.

Lease-validated mutation overloads are deferred: Core's current ITokenStore
contract takes no lease, and changing only FileTokenStore's surface would not
enforce this through Core. Record this as a future contract design question at
step 4; do not imply the current contract prevents an uncoordinated caller from
mutating. Matching-directory/key checks would be runtime checks even with a
lease parameter.

Support cooperating processes on stable local filesystems with functioning native
locks. Unix locks are advisory. Do not promise coordination with arbitrary writers,
network shares, mount/path substitution, or external deletion/replacement of lock
identities or their directory. Full ownership/symlink hardening remains checklist
step 5. All users of a store directory must follow the same lock protocol.

The chosen managed open does not provide O_NOFOLLOW or descriptor S_ISREG checks.
Rejecting symlinks and special files therefore remains a concrete step-5 task;
no race-safe rejection is claimed now. If native open is chosen later, use
O_NOFOLLOW, O_CLOEXEC, O_NONBLOCK (so a substituted FIFO cannot stall before
inspection), and fstat/S_ISREG checks with verified per-OS ABI layouts.

## Verification required before marking step 2 complete

Add a disposable subprocess helper and deterministic readiness/release handshakes;
the child writes `ACQUIRED\n` to stdout only after lease handoff and releases when
stdin reaches EOF. Kill crash-test holders with Process.Kill(entireProcessTree:
true), await termination, and bound all test waits with cleanup in finally.
Avoid assuming that a sleep proves a child owns the lock. Verify:

- Same-key contention within and across store instances and separate processes.
- Handoff after disposal: acquire A, dispose A concurrently from multiple threads,
  acquire B, then dispose A again. A third process must still be excluded by B.
- Cancellation and timeout preserve the current owner and do not poison later waits.
- Different keys and different directories progress independently.
- A killed holder releases ownership; the persistent file remains reusable.
- A child does not retain an unrelated parent's lease after exec.
- Stable file lifecycle, invalid timeouts, safe errors, and Unix creation modes.
- Explicit Unix locking still works when .NET implicit file locking is disabled:
  set DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1 in the child's environment before
  startup, because the setting is cached statically. Test both directions:
  disabled child holds/normal parent contends, then normal parent holds/disabled
  child contends. The child must not enable/disable the setting after opening files.
- Fake-time tests cover deadline-versus-handoff, cancellation, remaining-delay
  capping, and retry timing; real-process tests verify actual OS exclusion.

Run the tests through the existing Ubuntu/Windows/macOS CI matrix. Local tests
establish only the current OS result; record hosted verification separately.

## Alternatives considered

- Exclusive CreateNew ownership files: crash leaves stale ownership and cleanup
  introduces takeover races.
- Named mutexes: thread-affine ownership complicates async leases and introduces
  platform naming/security/domain questions.
- `FileStream.Lock`: platform-dependent byte-range semantics, including macOS
  support limitations, are a poor fit for this whole-session lease.
- Only managed semaphores: do not coordinate separate CLI processes.
- Unix `FileShare.None` alone: best-effort runtime behavior is insufficient for
  a lock provider that must not silently proceed without ownership.
- Assembly-wide DllImportResolver mapping a logical `libc` name: one indirection
  for all Unix imports, but once-per-assembly registration and interception of
  unrelated imports (kernel32) add global state for two call sites.
- Native open wrapped in SafeFileHandle: can bypass managed implicit locking and
  add O_NOFOLLOW, but requires per-OS flags and descriptor/ABI checks. The selected
  mechanism is managed FileShare.None plus explicit flock on the same handle.

## Sources

- [Windows CreateFile sharing and handle inheritance](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [Linux flock semantics, independent opens, and descriptor lifetime](https://man7.org/linux/man-pages/man2/flock.2.html)
- [Apple flock manual](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/flock.2.html)
- [.NET 10 Unix file-handle implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)
- [Linux NFS flock implementation](https://github.com/torvalds/linux/blob/master/fs/nfs/file.c)
- [.NET native library resolution](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading)
- [.NET 10 Unix errno-to-exception mapping](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs)
- [NativeLibrary.SetDllImportResolver single-registration rule](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver)
- [HRESULT_FROM_WIN32](https://learn.microsoft.com/en-us/windows/win32/api/winerror/nf-winerror-hresult_from_win32)
