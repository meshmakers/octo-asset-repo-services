using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.Extensions.DependencyInjection;

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
    /// Gets the Tenant Backup service
    /// </summary>
    public ITenantBackupService GetBackupService()
    {
        return GetService<ITenantBackupService>();
    }

    /// <summary>
    /// Gets the Blueprint service
    /// </summary>
    public IBlueprintService GetBlueprintService()
    {
        return GetService<IBlueprintService>();
    }
}
