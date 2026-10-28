# Local Keycloak browser fixture

A disposable public client with S256 required, no client secret, no password grant,
and the single exact redirect `http://127.0.0.1:43821/callback/`. The sample selects
MemoryOnly explicitly. Its session disappears when the CLI exits; no persistence,
refresh, device login, or remote logout is demonstrated yet.

The .NET file-based helper explicitly disables Native AOT. It runs using the
installed SDK/runtime and needs no platform-specific native compiler packages.

The version is pinned to `quay.io/keycloak/keycloak:26.8.1`, matching the
[official Docker guide](https://www.keycloak.org/getting-started/getting-started-docker).
It is a version tag, not a verified immutable digest. A **user-run live browser
smoke test passed on 2026-10-28**: login, cached acquisition, Available status, and
local logout. The agent session could not access the provider directly. Additional
live edge and platform checks remain pending; see the recorded checklist below.

From the repository root, with the pinned .NET SDK and a configured container
runtime running. The commands use your Docker CLI's selected context/endpoint;
the fixture does not select Docker Desktop, OrbStack, or a host OS for you.

```sh
dotnet run --file samples/keycloak/fixture.cs -- prepare
docker compose -f samples/keycloak/compose.yaml up -d
dotnet run --file samples/keycloak/fixture.cs -- wait
cat samples/keycloak/.local/credentials.txt
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- demo \
  --issuer http://127.0.0.1:8080/realms/cliauth \
  --client-id bijecta-cli --callback-port 43821 --allow-loopback-http
```

Enter the generated `learner` credentials in the browser. The command should show
login metadata, cached-token acquisition, Available status, and successful local
logout. It never prints tokens. `openid` is added automatically; profile/email and
offline_access are optional scopes. The client has only `basic` as a default scope
so Keycloak must not silently add unrequested named grants.

The HTTP exception applies only to IP-literal loopback destinations in this profile.
The server is exposed only on 127.0.0.1. This development realm permits local HTTP;
use HTTPS and real provider configuration elsewhere. Generated credentials live in
ignored `.local/` files. On Unix the directory is owner-only, as are the environment
and credential files. The import JSON is readable by the container user through its
bind mount; the enclosing directory restricts access on the host. On Windows the
files inherit the user's directory permissions. Do not commit them.

Repeat the command to check a second login. Occupy port 43821 to check that login
fails before opening the browser. Cancel while waiting to check cleanup. Local
logout does not end the provider's browser session, so repeat login may use SSO.
Record the date, OS/browser, runtime, image digest (`docker image inspect`), and observed
results in [the live checklist](../../tests/Bijecta.CliAuth.KeycloakTests/README.md).

Clean up the container and its disposable database:

```sh
docker compose -f samples/keycloak/compose.yaml down
```

Delete `samples/keycloak/.local/` explicitly if you also want new credentials on
the next run. The fixture's realm import applies when the realm does not exist;
regenerating files does not update an already-running realm.
