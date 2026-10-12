using System.Security.Claims;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    ///     Interim tenant guard of the Files routes (AB#6579). The platform tenant gate for user tokens is
    ///     LogOnly in asset-repo (AB#5054), so a user token issued for tenant A would otherwise reach
    ///     <c>/B/v1/files/...</c>, with its role names evaluated against B's policy table. Throws
    ///     <see cref="FileSystemException" /> (403 FORBIDDEN) for such a token.
    /// </summary>
    /// <remarks>
    ///     Mirrors the decision of <c>TenantAuthorizationMiddleware</c> (octo-common-services) for user tokens,
    ///     with the same preconditions, so exactly the callers the middleware would deny under Enforce are
    ///     refused: only a bearer-authenticated principal with a user subject (<c>sub</c> / NameIdentifier) is
    ///     checked, and its <c>tenant_id</c> claim must equal the route tenant (case-insensitive). Not touched
    ///     (as in the middleware): client-credentials / service tokens (no subject; governed by the service
    ///     token enforcement, AB#5032), cookie principals, and requests without a bearer header. A user token of a
    ///     <b>parent</b> tenant may address a child tenant (parent rule, resolved by the shared
    ///     <c>ITenantHierarchyReader</c> like the middleware does); child to parent, siblings and unrelated
    ///     tenants are refused. Removed when Enforce is armed platform-wide.
    /// </remarks>
    public async Task EnsureTenantMatchAsync(HttpContext httpContext, string routeTenantId,
        FilesTenantGuardMode mode = FilesTenantGuardMode.Enforce)
    {
        var user = httpContext.User;
        if (!httpContext.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            user.Identity is not { IsAuthenticated: true, AuthenticationType: { } authType } ||
            !authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            !user.HasClaim(c => c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier))
        {
            return;
        }

        var tokenTenantId = user.FindFirstValue("tenant_id");
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Parent-tenant rule (AB#5060 / AB#5708), same resolution as the middleware: a user of a parent tenant
        // may address its child tenant (ITenantHierarchyReader, one level; the system tenant covers the whole
        // estate). Not resolvable (reader not registered) means closed. Never the other way round.
        var account = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            httpContext.RequestServices?.GetService<ITenantHierarchyReader>() is { } hierarchy &&
            await hierarchy.IsChildTenantAsync(tokenTenantId, routeTenantId).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Files API: user token of account '{Account}' issued for tenant '{TokenTenantId}' addresses its " +
                "child tenant '{TenantId}'; allowed by the parent-tenant rule (AB#6579)",
                account, tokenTenantId, routeTenantId);
            return;
        }

        var clientId = user.FindFirstValue("client_id") ?? "<none>";
        var tokenTenant = string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId;
        if (mode == FilesTenantGuardMode.LogOnly)
        {
            logger.LogWarning(
                "Files tenant_mismatch (LogOnly, request allowed): user token of account '{Account}' (client " +
                "'{ClientId}') was issued for tenant '{TokenTenantId}' but addresses tenant '{TenantId}' (AB#6579)",
                account, clientId, tokenTenant, routeTenantId);
            return;
        }

        FilesAccessMetrics.RecordAccessDenied(routeTenantId, account, FilesAccessMetrics.ActionTenantMismatch);
        logger.LogWarning(
            "Files API access denied (403) tenant_mismatch: user token of account '{Account}' (client " +
            "'{ClientId}') was issued for tenant '{TokenTenantId}' but addresses tenant '{TenantId}' (AB#6579)",
            account, clientId, tokenTenant, routeTenantId);
        throw FileSystemException.Forbidden(
            $"Access to the files of tenant '{routeTenantId}' is denied: the token was issued for another " +
            "tenant. Sign in to the tenant to access its files.");
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
