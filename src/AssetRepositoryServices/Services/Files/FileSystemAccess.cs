using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Up-front data-permission check of the Files API (AB#6467). The engine filters reads and rejects writes
///     per entity anyway; this check answers a caller who may not touch the file system at all with a clear
///     403 FORBIDDEN before anything is buffered, looked up or compared (no 404 / 409 existence oracle, no
///     upload of a body that is refused afterwards). A caller with an owned-only grant passes: the engine
///     decides per entity.
/// </summary>
public class FileSystemAccess(IDataPermissionResolver permissionResolver, ICkCacheService ckCacheService)
{
    /// <summary>
    ///     Throws <see cref="FileSystemException" /> (403 FORBIDDEN) when the caller has no grant at all for
    ///     <paramref name="action" /> on one of <paramref name="ckTypeIds" />.
    /// </summary>
    public async Task EnsureAsync(ITenantRepository repository, RtSecurityContext securityContext,
        RtDataAction action, IEnumerable<RtCkId<CkTypeId>>? ckTypeIds = null)
    {
        if (securityContext.IsSystem)
        {
            return;
        }

        var table = await permissionResolver.GetPolicyTableAsync(repository).ConfigureAwait(false);
        if (!table.HasRules)
        {
            return;
        }

        foreach (var ckTypeId in (ckTypeIds ?? [FileSystemService.FileType]).Distinct())
        {
            var selfAndBase = RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames(ckCacheService,
                repository.TenantId, ckTypeId);
            var level = RtDataAccessEvaluator.Classify(table, selfAndBase, action, securityContext,
                includeAuditOnlyPolicies: false);
            if (level == RtDataAccessLevel.Denied)
            {
                throw FileSystemException.Forbidden(action == RtDataAction.Read
                    ? "Access denied: reading files and folders requires a file role (FileViewer or FileManagement) " +
                      "or an equivalent data permission."
                    : "Access denied: changing files and folders requires the role FileManagement " +
                      "or an equivalent data permission.");
            }
        }
    }
}
