# 0009: File-store protections, corrupt-record recovery, and orphan cleanup

Status: Accepted

Date: 2026-10-31

## Context

Plaintext persistence (ADRs 0005–0008) works end to end, but the file store
trusted whatever directory the host supplied. Existing directory permissions,
ownership, symlinks, network drives, and Windows ACLs were not checked. ADR 0006
deferred symlink/special-file and network-filesystem handling to this step, and
ADR 0008 left open whether login may replace a corrupt record.

Plaintext files cannot protect against programs running as the same user; any
plaintext CLI shares that limit, and protected storage remains the stronger
future option. These protections target other local users and mistakes:

- another user reading tokens from a readable directory or file;
- another user **planting** a session file. Session keys are deterministic, so
  anyone who knows the profile settings can compute the file name. In a shared
  directory such as `/tmp/myapp`, a planted valid-looking session would make the
  CLI silently use the attacker's account;
- misconfiguration, such as a world-writable or network-shared directory.

## Decision

### Directory trust (every operation that reads or writes)

The storage directory is checked on each lease acquisition, read, and write,
not once per instance, because it can change in between.

- **Symlinked directory:** allowed; its final target is validated instead, so a
  user's symlinked configuration directory keeps working. Parent directories are
  not checked (for example macOS `/tmp` → `/private/tmp`).
- **Unix:** the directory must not be writable by group or others. Readable by
  others (for example `0755`) is allowed: it reveals only hashed file names. New
  directories are still created `0700`.
- **Windows:** new leaf directories get an explicit, protected (non-inherited)
  ACL granting full control only to the current user, SYSTEM, and Administrators.
  Existing directories must be owned by the current user (or Administrators,
  which elevated administrators produce) and must grant no read, write, delete,
  or permission rights, directly or by inheritance, to any other principal.
  `%LOCALAPPDATA%` passes.
- **Network drives:** refused. Windows UNC paths and `DriveType.Network` volumes,
  and Unix mounts that .NET's `DriveInfo` classifies as `DriveType.Network`, are
  rejected, because ADR 0006's lock design needs native local semantics. Unknown
  or FUSE filesystems are not detected; if the mount table cannot be read, the
  check does not block.

### File trust

- **Session and lock files that are symlinks** (Unix symlinks, Windows reparse
  points) are refused.
- **Unix session files** must have no group or other permission bits, checked on
  the open handle. Together with the directory rule this makes "we could read it"
  imply "we own it" without native `stat` calls or per-OS struct layouts. The
  exception is root, which can read any file; running as root is documented as
  outside this guarantee.
- **Windows session files** must pass the same ACL rule as the directory.
- Staging files are created with `CreateNew` (exclusive), so they cannot be
  pre-planted.

These are managed checks between path inspection and open. Given the directory
rule, only processes running as the same user can race them, and those can read
the tokens anyway. Race-free native opens (`O_NOFOLLOW`, `fstat`) are not added.
FIFOs and other special files planted by the same user are likewise out of scope.

### Refuse, with a dedicated error

Unsafe locations are refused, never fixed silently: changing a host's directory
permissions is surprising, and a library cannot print warnings. Add
`AuthErrorCode.StorageInsecure` (appended to the enum), so hosts can tell users
to fix permissions or choose a private local directory rather than reporting a
generic storage failure. Messages remain path-free.

`FileTokenStore.DeleteAsync` does not run these checks: removing our own record
is always safe. Deleting a symlinked record removes the link, never its target.
Through `CliAuthenticator`, logout still works when only the session file is
unsafe (owner-only mode missing, or a symlink), because the coordinator removes
it without reading it. Logout does need the session lease, though, and lease
acquisition validates the directory: an insecure directory must be fixed (for
example `chmod 700`) before any operation, including logout. Skipping the lease
would break ADR 0006's coordination, and opening lock files in a directory
others can write is exactly what these checks prevent.

### Corrupt-record recovery: login replaces it

Supersedes ADR 0008's "login returns StorageCorrupt". Explicit login is a user
action, and the new session gets a fresh epoch, so it may replace a corrupt
record. To stay race-safe, the coordinator records that the record was corrupt
when login started and, at commit, replaces it only if it is **still** corrupt;
if a valid session was written meanwhile, login returns OperationSuperseded.
`UnsupportedSchema` (a newer version's record) and `StorageInsecure` still block
login; explicit logout removes a newer-schema record. Status and token
acquisition still return StorageCorrupt and never repair.

### Orphaned staging files

After every successful publication, the store deletes this key's orphaned
staging files, as logout already does (ADR 0007). Under the lease protocol no
cooperating writer is staging the same key. Orphans of keys never written again
remain (`0600` on Unix); no global or age-based sweep is performed, because it
could race writers of other keys.

## Consequences

Existing directories that are group- or world-writable, owned by another user,
on a network share, or carrying broad Windows ACLs now fail with StorageInsecure.
Synthetic test files are created with owner-only modes. The sample maps
StorageInsecure to the storage exit code with a fix-permissions hint.

## Alternatives considered

- Fix permissions automatically: convenient, but silently changes a host-owned
  directory and can mask an attack.
- Warn only: a library has no presentation channel; hosts would ignore it.
- Native `lstat`/`fstat` ownership checks and `O_NOFOLLOW` opens: race-free and
  covers root, but needs per-OS, per-architecture struct layouts and flags.
- Check parent directories (ssh `StrictModes` style): stronger against renamed
  parents, but rejects common layouts and needs ownership data we avoid reading.
- Global orphan sweep: removes more leftovers, but cannot know whether another
  key's writer is active.
