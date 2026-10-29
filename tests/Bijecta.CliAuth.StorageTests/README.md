# Storage tests

This executable xUnit/Microsoft.Testing.Platform project tests compact JSON
serialization and disk reads/staged replacement in `Bijecta.CliAuth.Storage.File`.
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

No real credentials or OS credential services are used. Power-loss durability,
process coordination, full permission/path hardening, and native protection tests
will be added with their implementation slices. Hosted OS success requires actual
CI results; it is not inferred from a local run.
