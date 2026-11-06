using Bijecta.CliAuth.ProviderTests.Auth0;
using Bijecta.CliAuth.ProviderTests.Entra;
using Bijecta.CliAuth.ProviderTests.Keycloak;

namespace Bijecta.CliAuth.ProviderTests.Harness;

/// <summary>Every provider with scenarios in this project. Add each new provider's definition here.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDefinition> All { get; } = [KeycloakScenarios.Provider, Auth0Scenarios.Provider, EntraScenarios.Provider];
}
