# 0003: Protocol dependency selection

Status: Accepted

Date: 2026-10-26

Accepted: 2026-10-27

## Context

We need browser PKCE and device login for developer-selected OAuth 2.0/OIDC
providers. We want to reduce protocol implementation work while retaining control
of our API and avoiding an expensive dependency on a vendor's future licensing or
release decisions. ADR 0001 keeps protocol integration internal; ADR 0002 requires
lazy discovery and explicit flow selection.

Current licensing alone does not settle the choice. Upstream behavior can change,
transitive dependencies can affect consumers, and replacing an abandoned dependency
costs engineering time. We evaluated runnable components before proposing a choice.

## Evidence

The retained [feasibility spikes](../../spikes/README.md) include source, locked
dependency graphs, a repeatable matrix script, observed gaps, and limitations.

- Microsoft component baseline: 15 probes pass on 8.23.0.
- Combined Duende/Microsoft candidate: the same 17 probes pass with OidcClient
  7.1.0 and 7.1.1, using IdentityModel 8.1.0 and Microsoft components 8.23.0.
- Both Duende NuGets declare Apache-2.0; source revisions and license references
  are recorded with the evidence.
- The candidate needs explicit nonce, signature-validation, and missing-ID-token
  enforcement. The Microsoft baseline also needs application-level discovery policy
  and a cancellation-aware document retriever.

Passing component probes do not establish complete flow or provider compatibility.
An explicit all-packages NuGet audit restore completed without audit warnings for
the current candidate graph after an earlier vulnerability-list command failed DNS.
This does not replace an independent security review.

## Decision

Use Duende.IdentityModel.OidcClient for browser-flow helpers and Duende.IdentityModel
for endpoint helpers behind internal adapters, with explicit Microsoft token/OIDC
validation. Keep session policy, capability checks, bounded transport, interaction,
loopback receiving, device polling, and refresh coordination under our control.

Adopt OidcClient 7.1.1, IdentityModel 8.1.0, and aligned Microsoft IdentityModel
components 8.23.0 as Core implementation dependencies. Central package versions and
lock files record the selected graph. Upstream types remain internal implementation
details; adding dependencies does not establish working authentication or provider
support.

Verify the adapter against a real Keycloak fixture for both flows before claiming
provider compatibility, without weakening the documented validation rules. Repeat
the dependency vulnerability review before release and on dependency upgrades.

## Alternatives considered

1. Microsoft IdentityModel plus .NET primitives: avoids Duende dependencies, but we
   own browser request/response and OAuth endpoint plumbing in addition to the
   lifecycle work common to both choices. The baseline demonstrates useful building
   blocks; it is not a full replacement implementation or a proof of effort parity.
2. Duende.IdentityModel helpers plus Microsoft validation, without OidcClient:
   saves endpoint boilerplate but leaves browser transaction orchestration to us.
   This reduces the high-level client dependency surface while keeping vendor risk.
3. The combined candidate: reuses more native-client behavior, but needs deliberate
   policy adapters and upgrade testing rather than trusting defaults.

## Consequences

Our initial protocol plumbing is smaller, but the dependency does not replace our
security policy or integration tests. Upstream API changes should be contained in
internal adapters; downstream dependency resolution can still cause incompatibility.

Pin and lock the tested development graph, review release and license changes, and
run adapter and packed-consumer tests before upgrading. Our lock files are not
inherited by consuming applications; do not describe them as a guarantee that every
consumer runs the tested dependency combination.

If future terms or maintenance become unacceptable, retaining the existing licensed
release may be an interim option, subject to security support and license conditions.
Replacing the adapter or maintaining a compliant fork requires explicit resources.
No dependency choice guarantees future free updates, including Microsoft dependencies.

## Acceptance and follow-up

Accepted after joint review of the feasibility evidence and explicit maintainer
approval. Retain the runnable spikes as supporting evidence and an upgrade baseline.
Core contracts, internal adapters, complete flow tests, and the real-provider fixture
remain implementation work; this ADR does not certify the package as production ready.
