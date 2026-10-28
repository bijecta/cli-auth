# Architecture

Bijecta.CliAuth is a library for developers adding browser and device authentication
to their own .NET CLIs. Applications configure their own OAuth 2.0/OIDC provider and
own the command framework, user interaction, and presentation.

**Current state:** package projects, build configuration, a sample CLI shell,
immutable Core configuration/contracts, process-local memory storage/locks, and a
concrete authenticator/session coordinator are implemented. Local status, cached-token
acquisition, and logout work publicly. Login commit/race behavior is verified with
an internal fake protocol; real provider login, browser launching, refresh, remote
revocation, protected backends, HTTP, and DI integration remain unavailable.
Responsibilities below include that planned work; see [Core usage](docs/usage/core-contracts.md)
and [coordinator usage](docs/usage/authenticator-composition.md) for the delivered boundary.

## Package boundaries

Arrows show project dependencies. Each integration depends on Core; platform
storage packages do not depend on one another.

```mermaid
flowchart BT
    MacOS[Storage.MacOS] --> Core[CliAuth.Core]
    Linux[Storage.Linux] --> Core
    Windows[Storage.Windows] --> Core
    Extensions[CliAuth.Extensions] --> Core
```

| Package | Responsibility |
|---|---|
| `Bijecta.CliAuth.Core` | Configuration, browser/device flow orchestration, internal protocol adapters, session coordination, refresh, public contracts, memory storage |
| `Bijecta.CliAuth.Storage.MacOS` | Keychain-backed protected persistence |
| `Bijecta.CliAuth.Storage.Linux` | Secret-Service-backed protected persistence |
| `Bijecta.CliAuth.Storage.Windows` | DPAPI-backed protected persistence |
| `Bijecta.CliAuth.Extensions` | Optional DI registration and authenticated HTTP integration |

The application explicitly selects a storage implementation. Core and Extensions
neither reference nor automatically select platform storage packages. A macOS-only
consumer can reference Storage.MacOS without bringing in the other backends.

`src/Shared.Storage` contains internal source compiled into each storage assembly.
It is not another public package. Platform dependencies belong only to their
respective backend projects. Storage implementations must share a consistent
atomicity and coordination contract; a custom implementation must satisfy it too.

## Ownership and extension points

Maintained protocol libraries handle supported protocol operations and validation
behind internal adapters. Core owns application-facing orchestration and session
policy. Neither third-party protocol types nor a replaceable protocol engine are
part of the initial public API.

Consumers configure issuer, client ID, scopes, selected flow, and supported policy
options. They can supply storage, a browser launcher, and device-code presentation.
These extension points adapt the host application without bypassing required
protocol validation. Exact API signatures remain subject to implementation review.
The selected protocol dependencies are Duende.IdentityModel.OidcClient 7.1.1,
Duende.IdentityModel 8.1.0, and Microsoft IdentityModel components 8.23.0. These
dependencies are registered in Core; the real adapters and flow implementations remain
planned work. The coordinator's protocol interface and deterministic test fake stay internal.

## Authenticator composition

The authenticator supports multiple named profiles sharing one explicitly
selected token store and its matching lock provider. Profile names are ordinal and
case-sensitive; duplicate names and incompatible persistence policies fail local
configuration. Session keys isolate the profiles within that store.

Direct construction uses CliAuthenticator with profiles, a store, optional matching
locks, optional browser/clock, and a bounded lock timeout. ITokenStore.PersistenceMode
is a read-only, I/O-free declaration checked against every profile. A store exposing
its own lock provider supplies the canonical locks; a conflicting provider is rejected.
Other custom stores require an explicit matching provider. DI convenience comes later.
There is no per-profile backend routing. Instances sharing a store must share its
coordination domain. See
[ADR 0004](docs/decisions/0004-authenticator-storage-and-profile-scope.md).

## Initialization and login

Configuration is validated locally when constructed or registered. Constructors
and DI registration perform no provider requests. Discovery happens lazily when an
operation needs provider communication.

For either login flow, Core checks the selected provider capability before starting
the interactive transaction. Discovery cannot guarantee that client or user policy
will permit the request; request-time rejection remains a distinct failure. Missing
optional metadata is interpreted according to the relevant standard, rather than
treated as proof of missing support. Flows never switch automatically.

- Browser login launches the system browser and receives an authorization-code
  response on a temporary loopback listener, using PKCE.
- Device login delivers the verification URI and user code to the application's
  presentation callback, then polls according to the protocol. The device code
  remains internal; this flow needs no loopback listener or automatic browser launch.

Both flows will validate the result and commit through the same session coordinator
and selected store. The implemented coordinator captures an epoch under the lock,
releases it for protocol interaction, then reloads/checks the epoch before an atomic
commit. Its fake tests verify that logout and a winning login supersede older logins.
Only explicit login initiates interactive authentication; real flow adapters are pending.

## Sessions, storage, and HTTP

The session coordinator owns silent cached-token acquisition, expiry decisions,
and login/logout concurrency. Refresh coordination is the next lifecycle feature;
a refresh-required acquisition currently returns UnsupportedCapability without
clearing the session or starting interaction. Protected stores must support atomic updates and
coordination between cooperating local processes. Memory storage is explicitly
selected and process-local. A protected-store failure returns an actionable error;
it never silently falls back to plaintext.

Status and ordinary local logout require no provider communication. A usable cached
token can be returned without discovery. Refresh and explicitly requested remote
revocation will need the provider, but do not initiate interactive login. Logout
commits deletion before optional revocation, releases the lease for remote work,
and reports its outcome separately. The fake-tested revocation path has a 30-second
cancellable deadline; the default public adapter currently reports Unsupported.

HTTP integration acquires tokens silently and attaches them only to configured
destinations. It does not automatically follow redirects or replay requests. The
application owns command output, exit codes, and any explicit retries. Credentials
and protocol secrets must be excluded from normal diagnostics.

## Samples and verification

System.CommandLine belongs to the sample CLI, not Core. The Keycloak fixture is
planned as the first real demonstration of both login flows. Provider support is
claimed only after testing; an optional single protected API may demonstrate HTTP
integration. Workers and token exchange are outside scope.

Current automated checks verify package boundaries plus local configuration,
isolation, redaction, expiry, process-local memory-store/lock behavior, and coordinator
composition, login/logout races, and fault handling through fakes. Real protocol,
native-storage, HTTP, and cross-process behavior tests will be added with those
features; compiling a backend project does not establish OS support.

## Decision records

This file describes the current architecture and is updated as it evolves. ADRs
record the reasons and tradeoffs behind individual decisions:

- [0001: Keep protocol integration internal](docs/decisions/0001-protocol-ownership-and-extension-boundaries.md)
- [0002: Initialize provider capabilities lazily](docs/decisions/0002-lazy-initialization-and-flow-capabilities.md)
- [0003: Protocol dependency selection](docs/decisions/0003-protocol-dependency-selection.md), supported by [runnable spikes](spikes/README.md)
- [0004: Authenticator storage and profile scope](docs/decisions/0004-authenticator-storage-and-profile-scope.md)
