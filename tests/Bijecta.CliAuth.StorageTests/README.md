# Storage tests

This executable xUnit/Microsoft.Testing.Platform project currently tests compact
JSON session serialization in `Bijecta.CliAuth.Storage.File`. Run it with the normal
solution test command in the root README.

Tests use synthetic credentials and in-memory serialized bytes. They cover session
round trips, redaction boundaries, credential-free markers, isolation-key binding,
malformed documents, unsupported schema, and size bounds. No real credential files
or OS credential services are used. File I/O, atomic replacement, process locks,
and native protection tests will be added with their implementation slices.
