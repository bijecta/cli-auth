# 0004: Share one storage setup across an authenticator's profiles

Status: Accepted

Date: 2026-10-27

## Context

The facade operates on named profiles, and session keys already isolate profiles
by application, issuer, client, scopes, and policy. We need to define how the
upcoming authenticator receives storage and coordinates mutations without making
the initial configuration API unnecessarily complex.

Multiple named profiles do not require multiple storage backends. Per-profile
backend selection would introduce additional registrations, routing, and validation
before a concrete consumer requirement exists.

## Decision

An authenticator supports multiple immutable named profiles and uses one explicitly
supplied token store with its matching session-lock provider for all of them.
Resolve profile names with ordinal, case-sensitive comparison and reject duplicate
names during local configuration. Session keys isolate records within the store.

All profiles must require the persistence policy supported by the selected storage
setup. Reject incompatible combinations as InvalidConfiguration rather than
changing storage behavior or falling back. ITokenStore.PersistenceMode declares the
backend's immutable policy without I/O, and construction checks it against every
profile. This declaration is an extension contract, not certification of a custom
backend's security.

Direct construction is the foundation. Optional DI registration will compose the
same dependencies rather than becoming a Core requirement. Construction performs
local validation only; provider discovery remains lazy under ADR 0002. Protocol
adapters remain internal under ADRs 0001 and 0003. CliAuthenticator accepts profiles,
a store, optional matching locks, optional browser/clock, and a lock timeout. If the
store implements the lock provider, use that canonical provider and reject a
conflicting one; other stores require explicit matching locks.

Applications needing different backends can create separate authenticators. If
instances share a store, they must also share its coordination domain; separate
authenticators must not create independent locks for the same stored session.

Defer backend selection per profile until a concrete use case justifies the added
configuration and routing. Do not automatically select a backend by OS.

## Alternatives considered

- One profile per authenticator: simpler in isolation, but would unnecessarily
  restrict the already named-profile facade and duplicate composition for profiles
  that can share a store safely.
- A backend registration per profile: flexible, but adds configuration complexity
  and more opportunities to mismatch storage and coordination.
- Automatically select or fall back between backends: convenient initially, but
  conflicts with explicit dependencies and persistence policy.

## Consequences

The initial authenticator has one storage/locking setup and a profile registry.
Profile isolation does not depend on creating separate stores. Mixing protected
and memory-only policies within one instance is intentionally unsupported.

Tests for the coordinator must cover profile routing/isolation, duplicate names,
persistence mismatch, and coordinated access when multiple instances share a store.
The initial coordinator implements direct construction, local operations, and
fake-protocol-tested login commits. Real provider adapters and DI registration are
separate follow-up features.
