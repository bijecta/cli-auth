# File storage: first implementation slice

This package currently implements only internal compact UTF-8 JSON serialization
of Core's `StoredSession` model. It references Core; Core does not reference it.
`ITokenStore`, `ISessionLockProvider`, and `MemoryTokenStore` remain in Core.

There is no public file store, filesystem I/O, or authenticator registration yet.
The existing Core storage contract requires atomic writes and matching locks;
those will be added in later steps before exposing a usable file backend. The
plaintext persistence policy will also need its own explicit public enum value.

See [the session file format](../../docs/usage/session-file-format.md) and
[ADR 0005](../../docs/decisions/0005-plaintext-persistence-first.md).
