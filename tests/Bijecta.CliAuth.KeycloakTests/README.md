# Keycloak browser and device integration tests

This xUnit/Microsoft.Testing.Platform project uses Testcontainers.Keycloak 4.16.0
and Microsoft.Playwright 1.64.1. It starts a fresh Keycloak 26.8.1 container and
headless Chromium, then exercises the public CliAuthenticator API with real
provider requests, loopback callbacks, signature validation, and memory storage.
These dependencies belong to this test project only and are not package dependencies
of Core or the sample CLI.

## Run locally

Use the pinned .NET SDK, PowerShell 7 for Playwright's browser-install script, and
a running local Docker-compatible Linux-container runtime. From the repository root:

```sh
dotnet restore Bijecta.CliAuth.IntegrationTests.slnx --locked-mode
dotnet build Bijecta.CliAuth.IntegrationTests.slnx --configuration Release --no-restore
pwsh tests/Bijecta.CliAuth.KeycloakTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test --solution Bijecta.CliAuth.IntegrationTests.slnx --configuration Release --no-build
```

On Linux, use `install --with-deps chromium` to install the browser's system dependencies
as well. CI does this on its Ubuntu runner. See [Playwright's .NET CI guide](https://playwright.dev/dotnet/docs/ci).

There is no manual Compose/prepare step for this suite. It creates its own realm,
public clients, generated credentials, mapped provider ports, and exact callback
registration. It never reads or changes the developer fixture's `.local/` files.
The callback port is reserved while the container starts, then released for Core;
the tests run serially in one collection to share that registration safely.

Testcontainers discovers the runtime and supports `DOCKER_CONTEXT`/`DOCKER_HOST`
configuration; the tests do not hardcode Docker Desktop, OrbStack, or a host OS.
Podman or other compatible runtimes may need socket/reaper configuration. Follow
[Testcontainers runtime configuration](https://dotnet.testcontainers.org/custom_configuration/).
This development fixture requires container ports reachable on local IPv4 loopback;
remote Docker hosts are outside its HTTP development policy. Missing runtime or
browser prerequisites fail the suite instead of silently skipping its assertions.

## What the tests prove

| Test | Assertions |
|---|---|
| Login/acquire/status/logout | Validated login, usable cached token, Available status, local removal, subsequent acquisition requires login |
| Repeat login with SSO | Local logout preserves browser SSO; the second login returns the same subject without submitting credentials again |
| Accept consent | The public consent client returns exactly the requested openid/profile scopes |
| Deny consent | Actual Keycloak denial maps to AccessDenied, saves no session, and releases the callback port |
| Cancel at login form | Cancelling while the real login form is visible saves no session and releases the listener |
| Occupied callback port | Core fails before calling the browser launcher and leaves storage empty |

The four device scenarios additionally cover:

| Test | Assertions |
|---|---|
| Device lifecycle with occupied callback port | Host callback completes real verification, signed tokens validate, cached acquisition/status/logout work, no browser-launch extension or loopback listener is used |
| Device denial | Actual provider denial maps to AccessDenied and saves no session |
| Cancel after device authorization | Real codes are obtained and presented; caller cancellation leaves no session |
| Disabled client grant | Endpoint rejection occurs before presentation with no browser fallback |

Device verification uses a browser owned by the test's presentation callback,
separate from Core's browser-launch extension (which throws if called). Dedicated
accept/deny clients isolate consent state. The device clients disable browser
authorization and omit the client PKCE mandate; the browser clients retain S256.
Keycloak 26.8.1 applies its PKCE mandate to device requests too, so this fixture
uses separate registrations for standard device flow. See the
[device walkthrough](../../docs/usage/device-login.md).

The PlaywrightSystemBrowser implements the existing ISystemBrowser host extension.
It navigates to Core's authorization URL and interacts with Keycloak's forms. Core
still handles the real protocol flow; no internal fake adapter is used. Browser
contexts are isolated per test. The SSO test intentionally keeps its context across
two logins; separate consent clients prevent accepted grants from affecting denial.

The fixture closes browser contexts, the shared browser/driver, and the container.
Testcontainers' resource reaper remains enabled as a cleanup backstop. Test and
startup deadlines bound failures. No traces, screenshots, saved authentication
state, or raw Playwright call logs are emitted because they can contain credentials
or authorization/callback URLs. Failures report the browser stage. Container startup
failures can report bounded startup-only logs with both generated passwords redacted.

## CI and solution separation

The existing Bijecta.CliAuth.slnx continues to run the deterministic tests on
Windows, Linux, and macOS without container/browser prerequisites. The separate
Bijecta.CliAuth.IntegrationTests.slnx references the same Core project and this
integration project. Its dedicated Linux CI job restores locked packages, builds,
installs Chromium, runs all ten tests, and checks formatting.

The extra solution is an execution boundary, not a separate implementation or a
NuGet package. It keeps heavyweight infrastructure out of the normal test command.

## Device validation — 2026-10-29

The final complete integration run passed all ten browser/device tests against
disposable Keycloak 26.8.1 and headless Chromium, with zero skips. Both solution
Release builds and locked restores passed. The deterministic suite has 258
passing tests, including 59 new device scenarios. This is local evidence; the
latest hosted CI result is not confirmed.

## Recorded validation — 2026-10-28

The integration Release build passed without warnings or errors. All six tests
passed against a disposable Keycloak container and headless Chromium in the local
development environment, with zero skips. The hosted Linux CI job has been added
and still needs its first run. The image tag is pinned; an immutable image digest
was not recorded.

Separately, the user previously completed the sample's live browser login, cached
acquisition, Available status, and local logout, and supplied terminal output.
That manually reported result is distinct from the automated suite above.

## Remaining desktop/process checks

The Playwright launcher replaces the native default-browser launcher. Native
Windows/Linux/macOS desktop-launch checks remain separate, as does a process-level
Ctrl+C/exit-130 check for the sample. The cancellation test here exercises the public
API's cancellation token. Other OIDC providers, native storage, and cross-process
locking are not established by this suite.
