# Protocol tests

Executable xUnit/Microsoft.Testing.Platform tests for the browser milestone. Run
with the solution test command in the root README.

Tests use disposable synthetic RSA/EC keys, a fake provider backchannel, real
loopback sockets, and FakeTimeProvider. They verify the complete adapter and stored
result, rejection without commit, PKCE/redirect binding, discovery/cache/trust,
unknown-key rotation limits, response sizes/deadlines, callback validation,
connection limits, cancellation, and safe errors. The public-constructor test also
uses the default real HTTP transport against a local synthetic provider.

No real credentials, captured provider tokens, native browser launch, or running
Keycloak are involved. Device tests will arrive with the device implementation.
See [live Keycloak checks](../Bijecta.CliAuth.KeycloakTests/README.md) for the separately
reported user-run smoke test and pending checks; do not infer live results from these tests.
