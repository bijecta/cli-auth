# Keycloak live checks

Use [the disposable fixture](../../samples/keycloak/README.md). This directory does
not currently contain an automated Keycloak test project. Browser protocol tests
use a synthetic provider and must not be reported as a live-provider run.

## User-run smoke test — passed, 2026-10-28

The user started the fixture and ran the sample on their development machine,
completed browser authentication, and supplied the terminal output. It reported:

- Login succeeded, with MemoryOnly persistence and the openid grant.
- Cached access-token acquisition succeeded with the same expiry and grants.
- Local status was Available.
- Local logout reported LocalSessionRemoved=true and NotRequested revocation.

This is a manually reported happy-path result, not an agent-executed or automated
Keycloak test. The image tag in the fixture is 26.8.1; the running image digest,
container-runtime version, and browser version were not captured. No tokens,
credentials, subject identifiers, or full captured provider responses are recorded here.

The attempt also exposed a sample issuer-option conversion bug, now fixed with
explicit absolute-URI parsing and safe InvalidConfiguration output. The .NET
fixture helper's JSON construction was corrected for file-app trimming/AOT checks.

The agent session could run the synthetic tests but was denied access to both the
container socket and TCP connections to the live provider on 127.0.0.1:8080.
The successful live result came from the user's terminal.

## Remaining live checks

Record the date, OS/browser, runtime, exact image digest, and observations for:

- Repeat login, including an existing browser SSO session.
- A fixed-port collision failing before browser launch.
- Ctrl+C while waiting closing the listener and exiting 130.
- Denied authorization creating no session, if the provider exposes a denial UI.
- Native launchers on the other target operating systems.

Automated synthetic tests cover callback validation, port collision, cancellation,
timeouts, token rejection, and session races. They do not establish native desktop
behavior on every platform or live compatibility with every provider.
