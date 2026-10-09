using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Default data of the platform file system (AB#6171 D8): the folder root <c>Files</c> in every tenant.
///     Ensured at every tenant start (one indexed query when it exists). Not a versioned service migration
///     on purpose: when System.Files could not be imported (e.g. the tenant's System model is older than
///     2.5 or ResolveFailed) the step logs and retries at the next start instead of failing the tenant.
/// </summary>
public class FileSystemDefaults(FileSystemService fileSystem, ISystemContext systemContext,
    ILogger<FileSystemDefaults> logger)
{
    /// <summary>
    ///     <see cref="EnsureDefaultRootAsync(ITenantContext)" /> for a tenant id. Never throws.
    /// </summary>
    public async Task<bool> EnsureDefaultRootAsync(string tenantId)
    {
        try
        {
            return await EnsureDefaultRootAsync(await systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Default file root 'Files': tenant '{TenantId}' not resolvable", tenantId);
            return false;
        }
    }

    /// <summary>
    ///     Every pod runs the tenant start (broadcast events), so two pods can seed "Files" at the same time
    ///     (no unique index on well-known names). The oldest root wins (the one FindRootAsync answers); empty
    ///     younger duplicates are erased.
    /// </summary>
    private async Task RemoveConcurrentDuplicatesAsync(ITenantRepository repository, ITenantContext tenantContext)
    {
        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        var duplicates = (await fileSystem.GetRootsAsync(repository, session).ConfigureAwait(false))
            .Where(r => string.Equals(r.Entity.RtWellKnownName, FileSystemConstants.DefaultRootWellKnownName,
                StringComparison.Ordinal))
            .OrderBy(r => r.Entity.RtCreationDateTime ?? DateTime.MaxValue)
            .ThenBy(r => r.Id.RtId.ToString(), StringComparer.Ordinal)
            .Skip(1)
            .ToList();
        foreach (var duplicate in duplicates)
        {
            if ((await fileSystem.GetChildrenAsync(repository, session, duplicate.Id).ConfigureAwait(false)).Count > 0)
            {
                logger.LogWarning("Duplicate default file root '{RtId}' in tenant '{TenantId}' has content; kept",
                    duplicate.Id.RtId, tenantContext.TenantId);
                continue;
            }

            var result = new Meshmakers.Octo.ConstructionKit.Contracts.OperationResult();
            session.StartTransaction();
            await repository.ApplyChangesAsync(session,
                [Meshmakers.Octo.Runtime.Contracts.EntityUpdateInfo<Meshmakers.Octo.Runtime.Contracts.RepositoryEntities.RtEntity>.CreateDelete(duplicate.Id)],
                Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions.Erase, result).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
            logger.LogInformation("Removed duplicate default file root '{RtId}' in tenant '{TenantId}'",
                duplicate.Id.RtId, tenantContext.TenantId);
        }
    }

    /// <summary>
    ///     Creates the root <c>Files</c> when it is missing. Never throws; answers whether the root exists.
    /// </summary>
    public async Task<bool> EnsureDefaultRootAsync(ITenantContext tenantContext)
    {
        try
        {
            var repository = tenantContext.GetTenantRepositoryAsAdmin();
            using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
            if (await fileSystem.FindRootAsync(repository, session, FileSystemConstants.DefaultRootWellKnownName)
                    .ConfigureAwait(false) != null)
            {
                return true;
            }

            var root = await repository.CreateTransientRtEntityAsync<RtFolderRoot>().ConfigureAwait(false);
            root.Name = FileSystemConstants.DefaultRootName;
            root.RtWellKnownName = FileSystemConstants.DefaultRootWellKnownName;
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, root).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
            logger.LogInformation("Created the default file root 'Files' for tenant '{TenantId}'",
                tenantContext.TenantId);

            await RemoveConcurrentDuplicatesAsync(repository, tenantContext).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Default file root 'Files' could not be ensured for tenant '{TenantId}' (is System.Files imported?); retried at the next start",
                tenantContext.TenantId);
            return false;
        }
    }
}
