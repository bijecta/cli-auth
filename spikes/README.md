# Protocol dependency feasibility

Evaluation date: 2026-10-26. This is runnable decision evidence, not production
code. The experiments are separate from the main solution. Their evidence supported
the dependency selection accepted on 2026-10-27; Core now references the selected
libraries, while the spike code remains separate from the package implementation.

## Question

Does Duende reduce the authentication protocol work we must maintain enough to
justify its dependency and vendor risks, compared with building the flows from
Microsoft IdentityModel components and .NET primitives?

## Reproduce

From the repository root, with the SDK in `global.json`:

```sh
bash spikes/run-matrix.sh
```

The script restores committed lock files, builds, and tests the Microsoft baseline,
then the same Duende probe source against OidcClient 7.1.0 and 7.1.1. It leaves the
candidate on the current version. Logs and XML results go to ignored `artifacts/spikes/`. Tests execute through the
standalone xUnit runner so the script does not require the `dotnet test` IPC pipe.

The separate `ProtocolFeasibility.slnx` runs the two current-version projects without
the previous-version matrix. All provider responses use in-memory HTTP handlers;
there are no real credentials, listeners, browser launches, or remote provider calls
in the tests. Package restore and vulnerability queries require network access.

## Evaluated versions

| Component | Version |
|---|---|
| SDK / local runtime | 10.0.401 / 10.0.12 |
| Local platform | macOS arm64 |
| Microsoft.IdentityModel.Protocols.OpenIdConnect and aligned JWT components | 8.23.0 |
| Duende.IdentityModel | 8.1.0 |
| Duende.IdentityModel.OidcClient | 7.1.0 and 7.1.1 |
| Test framework | xunit.v3.mtp-v2 4.0.2 |

An initial cached Microsoft 8.19.2 run helped investigate cancellation; the retained
matrix uses 8.23.0. Neither version is a production dependency selection.

## Executable findings

| Experiment | Result |
|---|---|
| Microsoft component baseline | 15 probes pass |
| Duende candidate, OidcClient 7.1.0 | 17 probes pass |
| Duende candidate, OidcClient 7.1.1 | 17 probes pass |

The baseline demonstrates signature, issuer, audience, expiry, algorithm,
authorized-party, and browser nonce rejection using Microsoft validators. A device
ID-token case succeeds without inventing a browser nonce. Lazy metadata/JWKS
retrieval and local discovery-cache reuse work with an injected HTTP client.

Two gaps were observed in the Microsoft components: retrieval alone accepts a
metadata issuer different from the configured issuer, so our policy must compare
it; the default HTTP document retriever does not forward the caller's cancellation
to its HTTP request in the evaluated versions. An injected `IDocumentRetriever`
that forwards cancellation passes the corresponding probe. Its sample code is
only a cancellation experiment, not a complete bounded production transport.

The Duende probes demonstrate lazy construction, S256 PKCE construction, explicit
nonce injection, state rejection before token redemption, and bridging successful
and rejected browser responses to Microsoft validation. Device helpers expose the
user code and pending response; the application still drives polling. Other probes
cover cancellation representation, one token POST on a server failure, and refresh
responses with and without a replacement refresh token.

Two successful probes deliberately demonstrate behavior we must reject in our
production OIDC adapter: browser preparation without explicit parameters has no
nonce, and a successful access-token response without an ID token is accepted by
the high-level client. A passing characterization probe does not mean this behavior
meets our contract. Our adapter must add nonce policy and reject a missing ID token
before session commit.

## Work saved and work retained

| Area | With Duende | With Microsoft components alone |
|---|---|---|
| Browser request/state/PKCE and code redemption | Existing native-client helpers, adapted to our policies | We implement the flow wiring |
| Device and refresh endpoint requests | Existing request/response helpers | We implement request construction and response processing |
| Signed ID-token and OIDC validation | Explicit Microsoft validator integration | Explicit Microsoft validator integration |
| Device polling, pending/slow-down/backoff/expiry | Ours | Ours |
| Capability checks, bounded discovery and transport | Ours | Ours |
| Browser launcher and constrained loopback receiver | Ours | Ours |
| Persistence, process races, refresh rotation, HTTP policy | Ours | Ours |

This supports a qualitative reduction in protocol plumbing, not a measured claim
about days saved or a complete implementation. Token/session policy remains ours.
The test fixtures and Microsoft validator bridge are intentionally minimal.

## Dependency and licensing risks

The actual downloaded NuGet metadata declares Apache-2.0 for both Duende packages.
Source revision for OidcClient 7.1.1: `ab00474eb5311c3b4431b992e7296e779f5904aa`.
Source revision for IdentityModel 8.1.0: `47a77f2dbda08f418ec95af04fc75529768dc1e2`.
The [upstream license](https://github.com/DuendeSoftware/foss/blob/imoc-7.1.1/LICENSE)
grants ongoing no-charge rights subject to its conditions. Future releases could
use different terms; retaining an Apache-licensed version does not guarantee future
maintenance, security fixes, or paid support. Review notices and each upgrade.

The runtime package graph in the current lock files has 10 packages for the
Microsoft baseline and 12 for the combined candidate, excluding test-only packages.
The combined candidate also selects newer versions of shared logging/DI abstraction
packages. Adding two library names therefore has effects beyond two isolated DLLs.

The previous/current release matrix proves the adapter code used here builds and
behaves consistently for those two versions. It does not prove future compatibility,
all upstream APIs, or a consuming application's resolved dependency graph. The
[7.1.1 release notes](https://github.com/DuendeSoftware/foss/releases/tag/imoc-7.1.1)
include a PAR behavior fix and dependency updates: even patch releases deserve review.
PAR and confidential-client authentication are not exercised by our public-client
spike.

Controls if we adopt the candidate:

- Keep upstream types behind internal adapters and retain an application-owned API.
- Lock our development/CI graph and review dependency and license changes before
  upgrades. Lock files do not constrain downstream NuGet consumers automatically.
- Run adapter contract tests on upgrades and package-consumer tests with resolved
  dependency combinations. Do not hide required runtime dependencies with PrivateAssets.
- Retain the Microsoft baseline as evidence of a replacement path. Removing Duende
  still requires implementation and verification; it is not a configuration switch.
- If an upgrade is unacceptable, assess retaining a supported pinned release,
  replacing the adapter, or maintaining a compliant fork. Freezing indefinitely is
  not a security-update strategy.

## Limits and remaining work

These are deterministic component probes, not a Keycloak/Okta compatibility claim,
full OIDC conformance suite, security review, or completed browser/device login.
No real provider/browser or Windows/Linux native test was run. Polling schedules,
size limits, redirects, nonce generation, callback hardening, refresh continuity,
unknown-key reload, storage, and process races require their production tests.

The initial vulnerability-list command failed when NuGet DNS was unavailable.
After restore recovered, an explicit locked restore with `NuGetAudit=true` and
`NuGetAuditMode=all` succeeded with no audit warnings for the current candidate graph.
This is a point-in-time check using available NuGet advisory data, not an independent
security review or a guarantee about future releases. Reproduce it separately:

```sh
dotnet restore spikes/ProtocolFeasibility/ProtocolFeasibility.csproj --locked-mode --force -p:NuGetAudit=true -p:NuGetAuditMode=all
```

## Source review

The tagged implementation was reviewed alongside executable probes:

- [Authorization request construction](https://github.com/DuendeSoftware/foss/blob/imoc-7.1.1/identity-model-oidc-client/src/IdentityModel.OidcClient/AuthorizeClient.cs)
- [Response processing and ID-token validator hook](https://github.com/DuendeSoftware/foss/blob/imoc-7.1.1/identity-model-oidc-client/src/IdentityModel.OidcClient/ResponseProcessor.cs)
- [Default validation policy](https://github.com/DuendeSoftware/foss/blob/imoc-7.1.1/identity-model-oidc-client/src/IdentityModel.OidcClient/Policy.cs)
- [Microsoft protocol validator](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.protocols.openidconnect.openidconnectprotocolvalidator)

The recommendation and alternatives are recorded in
[accepted ADR 0003](../docs/decisions/0003-protocol-dependency-selection.md).
