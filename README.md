# Bijecta.CliAuth

Reusable .NET packages for developers adding browser PKCE and device login to their
own CLI applications using an OIDC provider such as Keycloak or Okta.

**Status: early development. Core configuration, contracts, and memory storage are
implemented. Browser/device login and protected persistence are not available yet.**

## Packages

| Package | Intended responsibility |
|---|---|
| Bijecta.CliAuth.Core | Authentication, flow adapters, contracts, session coordination, memory storage |
| Bijecta.CliAuth.Storage.MacOS | Keychain-backed protected persistence |
| Bijecta.CliAuth.Storage.Linux | Secret-Service-backed protected persistence |
| Bijecta.CliAuth.Storage.Windows | DPAPI-backed protected persistence |
| Bijecta.CliAuth.Extensions | Optional HTTP and DI integration |

Each storage package references Core only; Core and Extensions do not select or
reference platform storage packages. Developers explicitly choose a backend or
provide a conforming custom store. Shared storage internals are source code, not
another public package. System.CommandLine is a sample dependency only.

See [Architecture](ARCHITECTURE.md) for responsibilities and runtime behavior, and
the [decision records](docs/decisions/) for the reasoning behind architectural choices.

## Development

Install the SDK pinned in `global.json`, then run from this directory:

```sh
dotnet restore Bijecta.CliAuth.slnx --locked-mode
dotnet build Bijecta.CliAuth.slnx --configuration Release --no-restore
dotnet test --solution Bijecta.CliAuth.slnx --configuration Release --no-build
dotnet run --project samples/Bijecta.CliAuth.Sample.Cli -- --help
```

Use `dotnet restore Bijecta.CliAuth.slnx --force-evaluate` to update committed lock
files intentionally after changing dependencies. Test execution uses xUnit v3
with Microsoft.Testing.Platform. CI runs the build and package-boundary tests on
Windows, macOS, and Linux; it does not yet validate native storage on those OSes.

See [Core contracts and memory sessions](docs/usage/core-contracts.md) for the first
implemented feature and its [compiled consumer example](samples/Bijecta.CliAuth.Sample.Cli/Examples/CoreUsage.cs).

## Implementation order

1. Core contracts, configuration validation, and memory storage (implemented foundation).
2. Internal protocol adapters using the [accepted dependency selection](docs/decisions/0003-protocol-dependency-selection.md).
3. Browser PKCE and device login with a Keycloak sample.
4. Platform persistence and cross-process session coordination.
5. Silent refresh, HTTP, and DI integration.

Both login flows are part of the first usable preview. Workers, client credentials,
and token exchange are outside scope. Dependency review, final license/author and
repository metadata, security reporting setup, native backend tests, and release
validation remain outstanding. Scaffold packages are not release artifacts.

The development workspace may keep its detailed design documents outside this
repository. They are implementation guidance, not build inputs or package content.
