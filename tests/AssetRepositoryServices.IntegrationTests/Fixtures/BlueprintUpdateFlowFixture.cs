using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for the blueprint update flow tests (AB#6315): real blueprint service and MongoDB, with a
/// local file system blueprint catalog pointing at <c>TestBlueprints/</c> in the output directory as the
/// only catalog (no GitHub catalogs, so no external state leaks in). Each test creates and drops its own
/// child tenant because an update changes the installed version of the tenant.
/// </summary>
public class BlueprintUpdateFlowFixture : AssetRepoFixture
{
    public const string TestBlueprintsRelativePath = "TestBlueprints";

    protected override async Task InitializeServicesAsync()
    {
        // The Mongo repositories and the blueprint support come from ServiceCollectionFixture
        // (AddOctoAssetRepositoryServices). No second AddRuntimeEngine() here: it would put the in-memory
        // repository provider on top of the MongoDB one, and the blueprint service needs the real tenant repositories.

        Services.RemoveAll<IBlueprintCatalog>();
        Services.AddTransient<IBlueprintCatalog, LocalFileSystemBlueprintCatalog>();

        // Fixture-local cache directory: the default one is shared by every test process on the machine.
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "TestBlueprintsCache",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        Services.Configure<LocalFileSystemBlueprintCatalogOptions>(options =>
        {
            options.RootPath = Path.Combine(AppContext.BaseDirectory, TestBlueprintsRelativePath);
            options.IsEnabled = true;
            options.CacheDirectory = cacheDir;
        });

        await base.InitializeServicesAsync();
    }

    public IBlueprintService GetBlueprintService() => GetService<IBlueprintService>();

    public ITenantBlueprintHistory GetBlueprintHistory() => GetService<ITenantBlueprintHistory>();

    public ITenantBlueprintInstallations GetBlueprintInstallations() => GetService<ITenantBlueprintInstallations>();

    public IRuntimeRepositoryProvider GetRuntimeRepositoryProvider() => GetService<IRuntimeRepositoryProvider>();

    /// <summary>
    /// Creates a fresh child tenant. The blueprint's CK model dependencies are installed on apply. The tenant
    /// id doubles as database name and must stay short (MongoDB application name limit).
    /// </summary>
    public async Task<string> CreateTenantAsync(string prefix)
    {
        var tenantId = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(26, prefix.Length + 9)];
        var systemContext = GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
        return tenantId;
    }

    public async Task DropTenantAsync(string tenantId)
    {
        try
        {
            using var session = await GetSystemContext().GetAdminSessionAsync();
            session.StartTransaction();
            await GetSystemContext().DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
