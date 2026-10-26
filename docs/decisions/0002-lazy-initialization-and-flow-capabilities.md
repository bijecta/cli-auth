# 0002: Initialize provider capabilities lazily

Status: Accepted

Date: 2026-10-26

## Context

Providers do not necessarily support the same grants, and server support does not
guarantee that a particular client or user is permitted to use a grant. Discovery
can reveal capabilities, but some metadata is optional and client policy may only
become visible when a request is attempted.

CLI startup and local commands must remain usable when the provider is offline.
Constructors and DI registration are unsuitable places for asynchronous network I/O.

## Decision

Validate locally knowable configuration when it is registered or constructed.
Perform no provider requests during construction, DI registration, or application
startup merely because authentication services are registered.

Initialize discovery lazily when an operation first needs provider communication.
For login, validate the explicitly selected flow before beginning its transaction.
Check relevant endpoints and advertised capabilities; reject known incompatibilities
with a typed UnsupportedCapability error. Do not validate every flow the package
implements when the application only enables or selects one.

Missing optional metadata is not a blanket rejection rule. Interpret metadata
according to the applicable standards. A missing required endpoint for the selected
flow is a capability failure. Discovery transport or validation failures are distinct
from an unsupported flow. Never relax protocol validation to accommodate missing data.

Treat discovery as evidence of server capabilities, not proof of client authorization.
Map request-time policy rejection to a safe, actionable error. Never silently switch
between browser and device flow.

Local status, ordinary local logout, and acquisition of a still-usable cached token
do not require discovery. Refresh and explicitly requested remote revocation may
need provider communication. No separate eager initialization API is required in
the initial contract. Cache successful discovery under the existing bounded cache
policy; a transient discovery failure must not permanently poison the instance.

## Alternatives considered

- Eager discovery during startup: earlier remote failure detection, but adds network
  latency and availability requirements to commands that do not need authentication.
- Trust configuration without capability checks: simpler setup, but postpones
  discoverable incompatibilities until interactive work has begun.
- Automatically choose or fall back to another flow: may improve availability, but
  changes the interaction selected by the application without explicit intent.

## Consequences

Registration is local and predictable. Commands that use local state remain usable
offline. The first provider-dependent operation pays discovery latency and can fail
then; successful startup does not certify remote configuration or client permissions.

Tests must distinguish local configuration failures, lazy discovery failures,
unsupported capabilities, and request-time client-policy failures. They must verify
that local operations do not trigger discovery.

## References

- [RFC 8414: Authorization Server Metadata](https://www.rfc-editor.org/rfc/rfc8414)
- [OpenID Connect Discovery](https://openid.net/specs/openid-connect-discovery-1_0.html)
- [RFC 8628: Device Authorization Grant](https://www.rfc-editor.org/rfc/rfc8628)
- [Okta device authorization configuration](https://developer.okta.com/docs/guides/device-authorization-grant/main/)
