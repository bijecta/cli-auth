# Shared storage internals

Shared internal C# source is compiled into each platform storage assembly through
`Shared.Storage.projitems`. This directory is not a project or NuGet package.
Keep platform-specific dependencies in the corresponding backend project.
The first persistence milestone prioritizes atomic file handling, process coordination,
and durable logout records for explicitly selected plaintext storage. Encryption
and native bindings are deferred by [ADR 0005](../../docs/decisions/0005-plaintext-persistence-first.md).
The plaintext backend's package placement and use of shared source remain implementation
design work; this directory currently contains no implemented file store.
