using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for Blueprint integration tests.
/// Extends AssetRepoFixture with Blueprint services.
/// </summary>
public class BlueprintTestFixture : AssetRepoFixture
{
    protected override async Task InitializeServicesAsync()
    {
        // Add Blueprint support before base initialization
        Services.AddRuntimeEngine()
            .AddMongoBlueprintSupport();

        // Strip the production-default IBlueprintCatalog registrations (LocalFileSystem,
        // EmbeddedResource, Public/PrivateGitHub). The GitHub catalogs point at real URLs
        // via their hardcoded option defaults and would otherwise leak external state into
        // tests that assume a fresh, empty catalog surface.
        Services.RemoveAll<IBlueprintCatalog>();

        await base.InitializeServicesAsync();
    }

    /// <summary>
    /// Gets the Blueprint Catalog Manager service
    /// </summary>
    public IBlueprintCatalogManager GetBlueprintCatalogManager()
    {
        return GetService<IBlueprintCatalogManager>();
    }

    /// <summary>
    /// Gets the Tenant Blueprint History service
    /// </summary>
    public ITenantBlueprintHistory GetBlueprintHistory()
    {
        return GetService<ITenantBlueprintHistory>();
    }

    /// <summary>
    /// Gets the Blueprint service
    /// </summary>
    public IBlueprintService GetBlueprintService()
    {
        return GetService<IBlueprintService>();
    }
}
