using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Migrations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Migrations;

/// <summary>
///     Seeds the default folder root <c>Files</c> of the platform file system in every tenant
///     (AB#6171 D8). Runs once per tenant through the asset repository's tenant setup, after
///     System.Files has been imported.
/// </summary>
[Migration(0, 1, AssetRepositoryServiceConstants.AssetServiceDefaultDataVersionKey)]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FilesRootMigration(ILogger<FilesRootMigration> logger) : MigrationBase
{
    public override async Task<MigrationResult> MigrateAsync(IOctoAdminSession adminSession,
        ITenantContext tenantContext)
    {
        try
        {
            await CreateOrUpdateAsync(adminSession, tenantContext, new RtFolderRoot
            {
                Name = FileSystemConstants.DefaultRootName,
                RtWellKnownName = FileSystemConstants.DefaultRootWellKnownName
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to create the default file root for tenant '{TenantId}'",
                tenantContext.TenantId);
            return MigrationResult.Failure($"Failed to create the default file root: {e.Message}");
        }

        return MigrationResult.Success();
    }
}
