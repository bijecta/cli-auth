# 0001: Keep protocol integration internal

Status: Accepted

Date: 2026-10-26

## Context

CLI developers need browser Authorization Code with PKCE and Device Authorization
Grant using their own OAuth 2.0/OIDC provider. They need to configure the provider
and adapt storage and user interaction to their application. Supporting those needs
does not require exposing a replaceable protocol engine.

We need an ownership boundary that lets us reuse maintained protocol libraries
without coupling consumers to their types or rebuilding protocol validation.

## Decision

Keep protocol integration and its adapters internal. Compose maintained libraries
for protocol operations and validation. Microsoft libraries are candidates; the
exact dependencies require evaluation for both flows before selection.

Core owns login orchestration, sessions, refresh coordination, cancellation, and
the errors exposed to consumers. Public configuration describes the issuer, client
ID, scopes, enabled flow, and supported policy options.

Expose deliberate extension points for storage, browser launching, and host-owned
device-code presentation. Do not expose a public protocol override or third-party
protocol types in the initial API. Configuration must not bypass required validation.

Provider capability checks and lazy initialization are covered in
[ADR 0002](0002-lazy-initialization-and-flow-capabilities.md).

## Alternatives considered

- Implement protocol operations and validation ourselves: more control, but a much
  larger security and maintenance responsibility.
- Expose a dependency directly: less adapter code, but consumers inherit its types,
  lifecycle, and compatibility constraints.
- Expose a public protocol interface now: supports replacement, but creates a
  complex compatibility contract without a demonstrated consumer requirement.

## Consequences

Consumers can customize application concerns without replacing authentication.
Internal adapters isolate dependencies and support deterministic tests. They add
integration code and do not remove the need to verify dependency capabilities.

Unsupported provider behavior may require a library change. Revisit public protocol
extensibility only when a concrete use case justifies the compatibility commitment.
