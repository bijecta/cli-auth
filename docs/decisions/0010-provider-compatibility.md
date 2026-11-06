# 0010: Provider compatibility

Status: Accepted (sections 1–2)

Date: 2026-11-01 (roadmap milestone 2, phase 0); section 2: 2026-11-02 (phase 1)

## Context

Milestone 2 has to show that the library works with real OAuth/OIDC providers, not only Keycloak: Duende
IdentityServer, Okta, Auth0, Microsoft Entra ID, and Google, with GitHub documented as unsupported. Each provider
has its own quirks, and several need decisions: Auth0's audience, Entra's multi-tenant issuer, Google's
installed-app client secret, and MFA requests.

ADR 0001 keeps protocol handling internal and says configuration must not bypass validation. Quirks are therefore
solved with typed, provider-neutral options or documented configuration, never with provider-specific branches in
Core or a generic dictionary of extra parameters.

This ADR records each provider-compatibility decision as a section, instead of one ADR per provider.

## 1. Verification policy (phase 0)

### What counts as tested

- A provider capability is marked ✅ in the [compatibility table](../providers/README.md) only after a passing
  **hosted** CI run, which the table links. Local runs are recorded as local evidence and the cell stays
  "not tested".
- Every provider runs the same scenarios (`ProviderScenarios` in `tests/Bijecta.CliAuth.ProviderTests`). A
  provider declares in its definition what it cannot do, such as consent denial or session reuse, and how it
  rotates refresh tokens (*None*, *Rotates*, *Strict*). A declared absence is an explicit skip with a reason, never
  a silently missing test.
- A provider's guide follows the [template](../providers/_template.md) and records the date, plan or version,
  library commit, and hosted run.

### Configuration and skip policy

- Provider tests read `CLIAUTH_<KEY>_*` environment variables locally, and the `providers` GitHub environment's
  secrets in CI. Credentials and tenant identifiers never go into the repository, logs, test output, or docs.
- If none of a provider's variables are set, its scenarios skip, naming the variables (not their values).
- A partial or invalid configuration fails. So does a provider listed in `CLIAUTH_REQUIRED_PROVIDERS`, which the
  hosted workflow sets, when its variables are missing; an unknown name in that list fails too. A missing or
  misspelled secret therefore cannot pass as a skip.

### Hosted CI

- Hosted providers run in their own workflow (`providers.yml`): manual dispatch, and, once the first hosted
  provider exists, a nightly schedule and push to main. Never on pull requests, since fork PRs get no secrets.
- It is **not** a required check. External providers can be unavailable or rate-limited, and the workflow does not
  run on pull requests, so it could not gate them. The table and the roadmap cite run IDs instead.
- Provider tests run on **Linux only**. Provider behaviour does not depend on the client OS, and the deterministic,
  process, and Native AOT jobs already cover Core on Ubuntu, Windows, and macOS. This is how milestone 2 meets the
  roadmap's three-OS rule.
- Self-hosted providers that need no secrets (Keycloak, and Duende IdentityServer per section 2) run on pull
  requests in the existing required integration job instead.

### Browser automation and bot challenges

- Playwright drives provider pages through a per-provider `ILoginPage` in the test project, never in Core. The
  redaction rules of the Keycloak suite apply: no traces, screenshots, page content, raw URLs, or Playwright call
  logs in output.
- Tests never try to get around a CAPTCHA, bot detection, or similar challenge. If a provider challenges headless
  logins from CI, the guide and the table record "cannot be automated" with the reason, which the milestone's
  definition of done allows. Alternatives are then decided per provider.

### Alternatives considered

- **One test project per provider:** stronger isolation, but duplicated harness code and lock files, and five
  Playwright installs in CI.
- **Skip whenever variables are missing, everywhere:** simpler, but a missing CI secret would look like a pass.
- **Run hosted providers on every pull request:** fork PRs get no secrets, and a provider outage would block
  unrelated changes.
- **Run hosted providers on all three OSes:** triples provider logins and rate-limit exposure without exercising
  any OS-specific code.

## 2. Duende IdentityServer as an in-process test host (phase 1)

### Decision

- Test Duende IdentityServer by hosting it **in the provider test process**: an ASP.NET Core host on an ephemeral
  IPv4 loopback port with in-memory clients, a generated test user and password, and a per-run RSA signing key. Duende
  has no official container image, and an in-process host needs neither Docker nor secrets, so the Duende scenarios
  run on every pull request in the existing required integration job.
- `Duende.IdentityServer` 8.0.10 and the ASP.NET Core shared framework are **test-only** dependencies of
  `Bijecta.CliAuth.ProviderTests`. Core's tested graph is unchanged (Microsoft IdentityModel 8.23.0,
  Duende.IdentityModel 8.1.0), and no package or sample references IdentityServer.
- **Licence.** Duende IdentityServer is source-available. Duende's documentation allows evaluation, development,
  test environments, and personal projects without a licence; production needs a licence or an approved Community
  Edition licence, and redistribution a separate one (checked 2026-11-02:
  [docs](https://docs.duendesoftware.com/identityserver),
  [Community Edition](https://duendesoftware.com/products/communityedition)). The host is test-only and never
  shipped, so no licence key is used. The project's own future licence does not change Duende's terms. The guide
  tells users that their production server needs its own licence. Recheck the terms on every IdentityServer upgrade.
- IdentityServer has no UI, so the tests include minimal login, consent, and device-verification pages built on its
  interaction services. Host logs are discarded because they can contain URLs, codes, and tokens.

### What the provider needed

No Core change. Each difference was configuration:

- Redirect URIs match exactly by default; the opt-in `AddAppAuthRedirectUriValidator()` with a client registered as
  `http://127.0.0.1` and PKCE required accepts any port and path, including Core's `/callback/`.
- `RefreshTokenUsage` defaults to `ReUse` (no rotation); `OneTimeOnly` is strict, and the default refresh token
  service rejects a consumed token with `invalid_grant`. The fixture uses `OneTimeOnly`.
- The default `SameSite=None` session cookie is dropped by Chromium over HTTP, so the loopback test host uses `Lax`.
- Discovery advertises the device and revocation endpoints and S256, but not `acr_values_supported`. MFA requests
  (phase 7) must therefore not depend on that discovery field; the returned `acr` claim is what counts.

### Alternatives considered

- **A separate host project or container:** keeps ASP.NET Core out of the test project, but adds a process to start
  and supervise, or an image to maintain, for no difference in what is tested.
- **IdentityServer4:** Apache-2.0, but archived and unpatched; not representative of current servers.
- **Duende's UI templates:** realistic pages, but far more test code to keep current than three minimal forms.

## 3. Auth0 (phase 3)

### Decision

- **Order.** Auth0 runs before Okta (maintainer's decision, 2026-11-03): Okta's Integrator Free Plan requires a
  business sign-up email, which the maintainer does not have yet. Okta's research and decisions stand, and its
  phase resumes once an account exists.
- Test a hosted free-plan tenant with two first-party **Native** applications (browser and device) on the default
  database connection, and a dedicated test user with MFA off. Bot Detection is off on the test tenant; the tests
  never get past a challenge (section 1).
- Auth0 has no any-port loopback redirect, so the browser application registers one fixed port (43823) and the
  tests pass it as `CLIAUTH_AUTH0_CALLBACK_PORT`. The port is not a secret and is set in the workflow; the issuer,
  client IDs, and test-user credentials are `providers` environment secrets.
- Rotation is on with a 0 s overlap period, so the tests verify *Strict* rotation (`invalid_grant` on reuse).
- Consent denial is skipped: first-party applications get no consent screen, and a third-party application needs
  tenant-wide changes for no extra library coverage.
- The test application's **ID Token Expiration** is raised to 86400 s. Without an `audience`, Auth0 issues a 24 h
  access token and a 10 h ID token; the refresh test's shifted clock would otherwise see the refreshed ID token as
  expired. Core's ID token lifetime check is correct and unchanged.

### What the provider needed

No Core change:

- The issuer ends in `/` (`https://<tenant>.<region>.auth0.com/`); Core compares the configured issuer exactly and
  trims the slash only to build the discovery URL, so configuring the slash is enough.
- Discovery advertises the device and revocation endpoints and S256 (and `plain`, which Core never sends).
- The device verification page asks the user to confirm the code before signing in; that is page automation only.

### Open question: `audience`

Core cannot send Auth0's `audience` authorization parameter, so the access token it obtains is only for Auth0's
`/userinfo`. A CLI that calls its own API needs an access token for that API. Adding a provider-neutral option for
extra authorization parameters (or a resource indicator, RFC 8707) is a public API change and is left for a
separate decision; the guide documents the limit.

## 4. Microsoft Entra ID (phase 4)

### Decision

- Test a single-tenant app in a free Entra directory (an Azure account's Default Directory). There are two
  public-client registrations (browser and device) with admin consent for `openid profile offline_access
  User.Read`, and a cloud-only member test user with no MFA registration. Security defaults, password-reset
  registration, and registration campaigns are off for that user, so sign-in never stops at "More information
  required" (section 1: the tests never get past an interactive challenge).
- **Single tenant only.** Core compares the issuer exactly, and the `common`/`organizations` discovery documents
  report a `https://login.microsoftonline.com/{tenantid}/v2.0` template, which no configured issuer equals.
  Supporting multi-tenant apps would need a per-provider issuer rule in Core, which section 1 rules out. It is
  not planned.
- Entra ignores the port of a loopback redirect URI, so one manifest entry, `http://127.0.0.1/callback/`, serves
  any port. The tests still pass 43823 (`CLIAUTH_ENTRA_CALLBACK_PORT`, set in the workflow) to match Auth0.
- Refresh tokens rotate, but the used one stays valid (*Rotates*).
- Consent denial is skipped: with admin consent the user is never asked.
- **Refresh test.** Access tokens last 60–90 minutes and the ID token 1 hour. Raising the ID token lifetime (as for
  Auth0) needs an Entra premium licence. Instead, `ProviderDefinition` gained an optional `RefreshAhead`, and for
  Entra the refresh scenario sets a 30-minute early-refresh window and moves the clock into it, rather than to
  just before access-token expiry. It still proves a silent refresh commits the next generation; Core's ID token
  lifetime check is unchanged.

### What the provider needed

One Core change, approved by the maintainer on 2026-11-06:

- **Granted scopes that were not requested are ignored instead of rejected.** Entra's token responses list every
  delegated permission the app has consent for, whatever was requested (RFC 6749 §3.3 allows a different granted
  scope). Core used to fail such logins with `ProtocolFailure`. It now records the requested scopes the provider
  granted. On refresh, it records the returned scopes the session already holds, so a refresh still never widens
  the grant (RFC 6749 §6). The stored-session checks against the profile's scopes are unchanged. ADR 0009's
  response rules are amended accordingly.

No Core change for the rest:

- The device `verification_uri` is `https://login.microsoft.com/device`, not on the issuer's host
  (`login.microsoftonline.com`). The existing `allowedEndpointOrigins` option covers it. The sample CLI has no
  flag for that option yet.
- Discovery has no `code_challenge_methods_supported` and no `revocation_endpoint`. Core accepts the first, and
  revocation is deferred.
- Sign-in pages keep inactive panels in the DOM, so the page automation identifies each page by its visible
  heading. The device confirmation page changed to "Approve sign-in on another device" (`#idBtn_Approve`), and the
  automation handles both versions.

## Later sections

Each later provider phase adds its decisions here: the policy on installed-app client secrets (Google), and the MFA
request options and claim validation (phase 7).
