# 0010: Provider compatibility

Status: Accepted (section 1)

Date: 2026-11-01 (roadmap milestone 2, phase 0)

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
- Self-hosted providers that need no secrets (Keycloak, and Duende IdentityServer if its licence allows) run on pull
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

## Later sections

Each provider phase adds its decisions here: the Auth0 audience option, the Entra multi-tenant issuer rule, the
policy on installed-app client secrets, and the MFA request options and claim validation (phase 7).
