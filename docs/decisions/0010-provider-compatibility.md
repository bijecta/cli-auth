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

## Later sections

Each later provider phase adds its decisions here: the Auth0 audience option, the Entra multi-tenant issuer rule, the
policy on installed-app client secrets, and the MFA request options and claim validation (phase 7).
