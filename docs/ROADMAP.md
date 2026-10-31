# Roadmap

This is the plain, ordered plan from here to a stable release. It says where the
project is going and what "done" means for each milestone. Detailed checklists for
an active milestone live in their own plan file (as [storage-plan.md](storage-plan.md)
did for storage); architecture choices live in [ADRs](decisions/).

Last updated: 2026-10-31.

## Current focus

- Done: Core contracts, browser PKCE and device login, session coordination,
  memory storage, and plaintext file storage with cross-process leases, logout by
  record removal, session listing, and private-location checks
  ([storage plan](storage-plan.md), ADRs 0004–0008).
- Active: milestone 0 (AOT guard), implemented on branch `feat/aot-guard`; local
  checks pass. Next action: open the PR, confirm the new `Native AOT publish and
  smoke` jobs on all three OSes, then propose adding them to main's required checks.
- Then: milestone 1 (refresh).
- Not now: packaging and releases (milestone 4), protected storage (milestone 5).

## Rules for every milestone

- **Native AOT compatible.** Libraries build with the trim, AOT, and single-file
  analyzers as errors (no trim or AOT warnings), and CI keeps an AOT-published sample working. No
  reflection-based JSON, configuration binding, or dynamic code; native calls use
  `LibraryImport`.
- **Verified on Ubuntu, Windows, and macOS** in hosted CI before a milestone is done.
- **Provider details stay out of Core**; console presentation stays out of libraries.
- **Few ADRs:** extend the ADR that covers a subject instead of adding one per step.

## Milestones

### 0. AOT guard — in progress

Lock in AOT compatibility while everything is still clean.

- [x] Library projects enable the trim, AOT, and single-file analyzers and mark
  themselves trimmable (`src/Directory.Build.props`), so new warnings fail the build.
  This is what `IsAotCompatible` sets, minus the SDK-versioned `ILLink.Tasks` package
  it would add to every lock file (which would break locked restore on each SDK patch).
- [x] The sample writes its output with source-generated JSON (`SampleOutput.cs`)
  instead of anonymous-type reflection serialization.
- [x] CI job `Native AOT publish and smoke` publishes the sample with
  `-p:PublishAot=true` on all three OSes (warnings are errors) and runs `list`,
  `status`, a corrupt-record `list`, and `logout` against a temporary directory.
- [ ] Hosted CI passes on all three OSes; then add the jobs to main's required checks.

Evidence: In progress. Release build zero warnings; 394 solution tests passed on
macOS (4 Windows-only skipped); a probe reflection call in Core failed the build with
IL2026/IL3050. The osx-arm64 AOT publish produced no trim/AOT warnings, and the CI
smoke script passed against the native binary. Locally, Homebrew's source-built SDK
links NativeAOT against OpenSSL and Brotli, so it needs
`LIBRARY_PATH=/opt/homebrew/opt/openssl@3/lib:/opt/homebrew/lib`; the official SDK
used in CI does not. The sample does not set `PublishAot` in its project, because
that adds runtime-specific entries to its lock file; the AOT job restores unlocked.

Done when: the AOT publish and smoke run pass in hosted CI on all three OSes.

### 1. Silent refresh

Without refresh, every expired access token means a new login, so the library is not
useful yet.

- Refresh expired or nearly expired access tokens with the stored refresh token,
  under the session lease, publishing a new generation.
- Handle refresh-token rotation, revoked or expired refresh tokens (signed out,
  login required), and transient provider failures (no session loss).
- Fence stale refreshes with epochs and generations (logout or login during refresh wins).
- Revisit the open question from ADR 0006/0008: lease-enforced store mutation APIs.
- Includes the remaining storage verification (storage-plan step 6): cross-process
  competing updates, login versus logout versus refresh through Core, and abrupt
  termination during locking and before publication.

Done when: refresh works end to end against Keycloak, and the process-race tests
pass on all three OSes.

### 2. Provider compatibility

Prove the library works with real OAuth/OIDC providers, not only Keycloak.

- Real integration tests (browser and device flows, refresh, revocation where
  supported) against Keycloak, Duende IdentityServer, Okta, Auth0, Microsoft Entra ID,
  and Google.
- Document each provider's quirks and fix generic issues without provider-specific
  code in Core.
- MFA requests: optional `acr_values`, `max_age`, and `prompt=login` per profile or
  login, plus validation of the returned `acr`/`amr` claims so an ignored request is
  detected. MFA itself always happens on the provider's page.
- Docs: one configuration guide per provider (app registration, redirect URIs,
  device flow, scopes, refresh) and a compatibility table in the README.

Done when: each listed provider has a passing integration run (or a documented
reason it cannot be automated) and a published guide.

### 3. Integration layer

Make the library easy to adopt in a real CLI.

- DI registration (`AddCliAuth`) in Extensions, with source-generated configuration
  binding (AOT).
- An HTTP message handler that attaches access tokens, refreshes when needed, and
  surfaces step-up challenges (RFC 9470 `insufficient_user_authentication`, Entra
  claims challenges) to the host.
- A System.CommandLine example CLI calling a real API, with `login`, `logout`,
  `status`, `list`, and `token` commands.
- Small additions: a token-printing API for scripts, an environment-variable token
  override for CI, automatic device flow when no browser is available, and custom
  `HttpClient`/proxy configuration.

Done when: the example CLI calls a protected API with automatic refresh, AOT-published,
on all three OSes.

### 4. First preview release

Packaging on its own branch, after milestones 1–3.

- Package metadata, license, security policy, and dependency review.
- Pack and smoke-test the packages from a clean consumer project (including an AOT
  consumer).
- Publish a preview only with the maintainer's explicit approval.

### 5. Protected storage

Keyring-backed storage: macOS Keychain, Windows (DPAPI/Credential Manager), and
Linux Secret Service, decided first in a new ADR (native services, encryption, key
lifecycle, headless Linux).

- Implement the platform storage packages behind the existing `ITokenStore` contract.
- Document moving from plaintext to protected storage (ADR 0008: new key, log in again).

### Later: toward 1.0

- The 1.0 gate: API review, stable session-file and key formats, full docs.
- Optional biometric unlock for protected storage.
- DPoP (RFC 9449) sender-constrained tokens, with the key in protected storage.

## Ideas, if users ask

Not planned; each needs a reason before it is scheduled.

- Pushed authorization requests (PAR, RFC 9126) for providers that require them.
- Resource indicators (RFC 8707): tokens for several APIs from one login.
- Several accounts per profile with switching.
- Logging and OpenTelemetry hooks with token redaction.

## Out of scope

- Prompting for MFA codes or passwords in the library; MFA belongs to the provider.
- Background workers, client credentials, and token exchange (RFC 8693), as
  recorded in the project guidance.
- Dynamic client registration and provider-side single logout.
