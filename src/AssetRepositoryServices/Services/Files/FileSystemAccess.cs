using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
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
public class FileSystemAccess(
    IDataPermissionResolver permissionResolver,
    ICkCacheService ckCacheService,
    ILogger<FileSystemAccess> logger)
{
    /// <summary>
    ///     Makes a refused Files API request visible (AB#6575): counter <c>octo.files.access_denied</c> and a
    ///     structured warning with tenant, account (subject id / client id - never a token) and action.
    /// </summary>
    public void RecordDenied(string tenantId, RtSecurityContext securityContext, RtDataAction action)
    {
        var account = string.IsNullOrEmpty(securityContext.SubjectId) ? "unknown" : securityContext.SubjectId;
        var actionName = action switch
        {
            RtDataAction.Read => FilesAccessMetrics.ActionRead,
            RtDataAction.Delete => FilesAccessMetrics.ActionDelete,
            _ => FilesAccessMetrics.ActionWrite
        };
        FilesAccessMetrics.RecordAccessDenied(tenantId, account, actionName);
        logger.LogWarning(
            "Files API access denied (403): tenant '{TenantId}', account '{Account}', action '{Action}'. " +
            "Grant the role FileManagement (write) or FileViewer (read) to the account",
            tenantId, account, actionName);
    }

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
                RecordDenied(repository.TenantId, securityContext, action);
                throw FileSystemException.Forbidden(action == RtDataAction.Read
                    ? "Access denied: reading files and folders requires a file role (FileViewer or FileManagement) " +
                      "or an equivalent data permission."
                    : "Access denied: changing files and folders requires the role FileManagement " +
                      "or an equivalent data permission.");
            }
        }
    }
}
