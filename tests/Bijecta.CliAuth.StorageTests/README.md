# Storage tests

This executable xUnit/Microsoft.Testing.Platform project tests compact JSON
serialization, disk reads/staged replacement, and matching session leases in
`Bijecta.CliAuth.Storage.File`.
Run it with the normal solution test command in the root README.

Serializer tests use synthetic credentials and in-memory bytes. They cover round
trips, redaction boundaries, credential-free markers, isolation-key binding,
malformed documents, unsupported schema, and size bounds.

FileTokenStore tests use disposable temporary directories and verify construction
and missing-read side effects, path validation, replacement, readback through
another instance, isolation, cancellation, malformed files, safe filesystem errors,
and owner-only modes for newly created Unix files/directories.

Atomic-write tests cover visibility before publication, cancellation/failure
preserving the original file, owned-temporary cleanup, replacement with an open
reader, and restrictive Unix replacement modes. The project is in the normal
solution; the existing Ubuntu/Windows/macOS CI matrix executes this same suite.
Windows behavior is not skipped. Unix-mode assertions apply only on Unix hosts.

Lock tests verify same/different-key behavior across instances and processes,
timeout/cancellation, persistent identities and Unix modes, safe errors, process
termination, non-inheritance of unrelated leases, and concurrent/repeated disposal.
FakeTimeProvider tests exercise capped delays, retry scheduling, cancellation,
and cancellation/deadline before ownership handoff without waiting for wall time.

`Bijecta.CliAuth.LockProcess` is a separate executable helper built by the solution
and copied to the test output's LockProcess directory. It writes WAITING before
acquisition and ACQUIRED only after handoff; stdin EOF releases a held lease.
Probes expecting contention use a 150 ms budget; probes expecting acquisition use
5 s so transient third-party handles (for example Windows antivirus) cannot fail
them. The helper starts through DOTNET_HOST_PATH when the SDK provides it.
Crash tests kill its entire process tree and wait for exit. All helper waits are
bounded and cleanup runs even if the test token expires. Child environments set
DOTNET_SYSTEM_IO_DISABLEFILELOCKING before startup; both parent-held/child-waiting
and child-held/parent-waiting cases exercise explicit Unix locking.

Logout tests verify record removal across instances, missing/repeated logout,
removal of unreadable records, this-key-only orphan cleanup, cancellation,
sanitized failures, and removal with an open reader followed by a new write.

No real credentials or OS credential services are used. Power-loss durability,
full permission/path hardening, and native protection tests
will be added with their implementation slices. Hosted OS success requires actual
CI results; it is not inferred from a local run.

## Windows open-reader regression

The initial Windows matrix run failed ExistingReaderRetainsOldFileWhileNewReaderSeesReplacement:
legacy File.Move replacement rejected the open target despite delete sharing.
The assertion and strict reader sharing flags are retained; both ordinary and
Unicode directory cases now exercise Windows POSIX rename publication. Three
platform-independent buffer tests verify native pointer alignment, UTF-16 byte
length, replacement/POSIX flags, and ordinary/Unicode/extended path encoding.
Actual native Windows behavior still requires a hosted Windows rerun; passing the
buffer tests on macOS is not a Windows integration result.
