# Provider integration tests

Real logins against OAuth/OIDC providers other than the disposable Keycloak suite: hosted SaaS tenants and
self-hosted servers. The rules (skip policy, evidence, bot challenges) are in
[ADR 0010](../../docs/decisions/0010-provider-compatibility.md); results go in the
[compatibility table](../../docs/providers/README.md).

## Layout

| Path | Contents |
|---|---|
| `Harness/` | Environment reading and skip policy, the shared Chromium (`BrowserHost`), `ProviderBrowser` (the `ISystemBrowser` host extension plus device verification), and `ProviderScenarios`, the standard scenarios |
| `HarnessTests/` | Unit tests for the skip policy and redaction; they need no browser or provider |
| `<Provider>/` | One folder per provider: its `ProviderDefinition`, a sealed `ProviderScenarios` subclass, and its `ILoginPage` with the page steps. Page steps never go into Core |

`Duende/` hosts Duende IdentityServer 8 in the test process (`DuendeFixture`), with minimal login, consent, and
device pages (`DuendeUi`). It needs no variables or Docker, so it always runs, including in the required CI job.
Its settings come from the fixture through `ResolveSettings()` instead of the environment, and it is not in
`ProviderCatalog` (which lists environment-configured providers). It also checks the discovery document.
See the [Duende guide](../../docs/providers/duende.md).

`Keycloak/` is the harness's reference provider. It runs against the developer fixture in `samples/keycloak`. The
CI-required, disposable Keycloak coverage stays in `Bijecta.CliAuth.KeycloakTests`.

## Scenarios

Every provider runs the same five scenarios from `ProviderScenarios`. Each one skips with a stated reason when the
provider is not configured, or when its definition declares the capability absent.

| Scenario | Asserts |
|---|---|
| `BrowserLoginCachesTokenReportsStatusAndLogsOut` | Validated login, cached token, `Available` status, local logout, then `ReauthenticationRequired` |
| `DeviceLoginCompletesWithoutBrowserLaunchAndCachesToken` | The device code is presented once, the user's verification completes, the token is cached; Core never launches a browser |
| `ExpiringTokenIsRefreshedSilently` | A refresh token is issued; after moving the clock into the expiry margin, a refresh commits the next generation with the same epoch. *None* keeps the refresh token, *Rotates* and *Strict* change it, and *Strict* rejects reuse with `invalid_grant` |
| `DeniedConsentSavesNoSession` | Denying on the consent client gives `AccessDenied` and no session |
| `SecondLoginReusesProviderSession` | After local logout, a second login completes from the provider session without credentials and returns the same subject |

## Configuration

Each provider reads `CLIAUTH_<KEY>_*` variables. `<KEY>` is the provider's key, for example `OKTA`.

| Variable | Required |
|---|---|
| `ISSUER` | Yes. Exact issuer; HTTPS, or HTTP for an IP-literal loopback address only |
| `CLIENT_ID` | Yes. The browser-flow client |
| `USERNAME`, `PASSWORD` | Yes. A dedicated test user with MFA off |
| `CONSENT_CLIENT_ID` | Yes, if the provider declares it can deny consent: a client that always asks for consent |
| Provider extras | Yes, if the provider's definition lists any (for example an audience or tenant) |
| `DEVICE_CLIENT_ID` | No. Defaults to `CLIENT_ID` |
| `CALLBACK_PORT` | No. Defaults to 0 (any free port); set it when the provider needs an exact registered port |
| `SCOPES` | No. Space-separated; defaults to the provider's definition |

### Skip policy

| Situation | Result |
|---|---|
| None of the provider's variables set | The provider's scenarios **skip**, naming the variables |
| Some set, some missing or invalid | **Fail**: a misconfiguration must not look like a skip |
| Provider named in `CLIAUTH_REQUIRED_PROVIDERS` (comma-separated, case-insensitive) but not configured | **Fail** |
| An unknown name in `CLIAUTH_REQUIRED_PROVIDERS` | **Fail** (`RequiredProvidersInThisEnvironmentAreKnown`) |

The hosted workflow sets `CLIAUTH_REQUIRED_PROVIDERS`, so a missing or misspelled secret fails the job.

### Redaction

Issuers, client IDs, tenant identifiers, and credentials are treated as secrets. `ProviderSettings.ToString()`
prints none of them; skip and failure reasons name variables, never their values. Browser failures report only the
provider name and the stage, never Playwright's call log, page content, URLs, screenshots, or traces. Login failures
log the `AuthErrorCode` and the provider's OAuth error code only.

## Run locally

Prerequisites are the same as for the Keycloak suite: the pinned SDK, and PowerShell 7 for Playwright's install
script. From the repository root:

```sh
dotnet restore Bijecta.CliAuth.IntegrationTests.slnx --locked-mode
dotnet build Bijecta.CliAuth.IntegrationTests.slnx --configuration Release --no-restore
pwsh tests/Bijecta.CliAuth.ProviderTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test --project tests/Bijecta.CliAuth.ProviderTests --configuration Release --no-build
```

With no variables set, the harness tests run and every provider scenario skips. Chromium is only started when a
provider is configured. To run the Keycloak reference scenarios, start the developer fixture
(`samples/keycloak/README.md`) and set the variables from the [Keycloak guide](../../docs/providers/keycloak.md#integration-test-variables).

## Adding a provider

1. Add `<Provider>/<Provider>Scenarios.cs` with a `ProviderDefinition` (name, key, rotation, consent and session
   capabilities, default scopes, required extras), a sealed `ProviderScenarios` subclass, and an `ILoginPage`.
   Override `CreateProfile` only for provider-neutral options such as `allowedEndpointOrigins`. A self-hosted
   provider with its own fixture overrides `ResolveSettings()` and implements `IClassFixture<TFixture>`.
2. Add the definition to `ProviderCatalog.All`.
3. Add the provider's secrets to the `providers` environment, map them in `.github/workflows/providers.yml`, and
   add the provider to the workflow's required list.
4. Never automate around a CAPTCHA or bot challenge. If the provider challenges headless logins, record it as
   "cannot be automated" in the guide and the table.

## CI

`Bijecta.CliAuth.IntegrationTests.slnx` contains this project, so the required Linux job
*Keycloak browser and device integration* builds it and runs the harness tests and the Duende scenarios
(environment-configured provider scenarios skip there).
Hosted providers run in `.github/workflows/providers.yml`, never on pull requests and not as a required check.

## Validation — 2026-11-01 (local, macOS)

- Without variables: 29 harness tests passed, 5 provider scenarios skipped with the reason.
- Against the developer Keycloak fixture (26.8.1, freshly imported realm), three consecutive runs: browser login,
  device login, refresh (*Rotates*), and SSO second login passed; consent denial skipped, as declared.
- An earlier device run timed out once at the full 3-minute test limit, before the Keycloak page step handled
  the device consent page. It did not recur in the three runs after the fix. This is local evidence only.

## Validation — 2026-11-02 (local, macOS): Duende IdentityServer 8.0.10

- The six Duende tests (five scenarios and the discovery check) pass; three consecutive runs of the whole project:
  34 passed, 5 skipped (the Keycloak reference scenarios without variables).
- First runs failed with every login stopping after sign-in: Chromium dropped IdentityServer's `SameSite=None`
  cookie over HTTP. The fixture now uses `Lax`. Discovery does not advertise `acr_values_supported`.
- Hosted: run 41518115392 (pull request #9) passed with the same counts: 46 passed, 5 skipped in the integration
  solution, including all six Duende tests.
