using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Security;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     Repairs the system tenant registry (AB#6971, AB#6941) and reviews tenant-database ownership (AB#8796).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/tenants")]
[ApiController]
[ApiVersion("1.0")]
public class TenantRegistryController : ControllerBase
{
    private readonly ISystemContext _systemContext;
    private readonly ILogger<TenantRegistryController> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    public TenantRegistryController(ISystemContext systemContext, ILogger<TenantRegistryController> logger)
    {
        _systemContext = systemContext;
        _logger = logger;
    }

    // POST system/v1/tenants/reconcile-registry?apply=false
    /// <summary>
    ///     Finds tenants that a parent tenant lists in its own registry but that are missing in the system
    ///     registry, and (with <c>apply=true</c>) inserts the missing system registry rows. <b>Dry run is the
    ///     default</b>: nothing is written. Rows are only ever inserted, never changed or deleted; rows that disagree
    ///     with the parent's registry and registries that cannot be read are returned as conflicts and left alone.
    ///     Idempotent: a second call inserts nothing.
    /// </summary>
    /// <remarks>
    ///     A tenant without its system registry row is invisible to everything that iterates the registry (client
    ///     mirror provisioning, token cleanup, CORS, health checks, <c>GetTenants -a</c>) although the recursive
    ///     listing (<c>GetTenants -r</c>) still shows it. The call crosses tenant boundaries, so besides the system
    ///     read-write scope the caller must be signed in to the system tenant (<c>tenant_id</c> claim) and, with a
    ///     user token, hold the role <c>AdminPanelManagement</c> (<see cref="SystemAdministratorGuard" />).
    ///     Runbook: dry run, review inserts and conflicts, then <c>apply=true</c>.
    /// </remarks>
    /// <param name="apply">False (default): report only. True: insert the missing rows.</param>
    [HttpPost("reconcile-registry")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TenantRegistryReconcileReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReconcileRegistry([FromQuery] bool apply = false)
    {
        if (!SystemAdministratorGuard.IsSystemAdministrator(User, _systemContext))
        {
            return Forbid();
        }

        try
        {
            using var session = await _systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            TenantRegistryReconcileResult result;
            try
            {
                result = await _systemContext.ReconcileTenantRegistryAsync(session, apply);
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }

            await session.CommitTransactionAsync();

            _logger.LogInformation("Tenant registry reconcile ({Mode}): {Summary}",
                apply ? "apply" : "dry-run", result.Summary);
            return Ok(TenantRegistryReconcileReportDto.From(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Tenant registry reconcile failed");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new InternalServerErrorDto("The tenant registry reconcile failed; see the service log."));
        }
    }

    // POST system/v1/tenants/review-ownership?stamp=false
    /// <summary>
    ///     Lists this instance's tenant databases whose ownership is not unambiguous on the shared MongoDB server:
    ///     no ownership marker, the marker of another OctoMesh instance, or a registration in another instance's
    ///     system database as well. With <c>stamp=true</c> the marker of this instance is written only for the
    ///     unambiguous databases (no marker, registered only here); every other database is left untouched and
    ///     stays listed. <b>Listing is the default</b>: nothing is written. Idempotent: a second call stamps nothing.
    /// </summary>
    /// <remarks>
    ///     A double registration is resolved by detaching the tenant in the instance that must not have it
    ///     (never by deleting it: delete drops the shared database). Same access rule as the registry reconcile:
    ///     system read-write scope, signed in to the system tenant and, with a user token, the role
    ///     <c>AdminPanelManagement</c> (<see cref="SystemAdministratorGuard" />).
    /// </remarks>
    /// <param name="stamp">False (default): list only. True: stamp the unambiguous unmarked databases.</param>
    [HttpPost("review-ownership")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TenantOwnershipReviewReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReviewOwnership([FromQuery] bool stamp = false)
    {
        if (!SystemAdministratorGuard.IsSystemAdministrator(User, _systemContext))
        {
            return Forbid();
        }

        try
        {
            using var session = await _systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            TenantOwnershipReviewResult result;
            try
            {
                result = await _systemContext.ReviewTenantOwnershipAsync(session, stamp);
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }

            await session.CommitTransactionAsync();

            _logger.LogInformation("Tenant ownership review: {Summary}", result.Summary);
            return Ok(TenantOwnershipReviewReportDto.From(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Tenant ownership review failed");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new InternalServerErrorDto("The tenant ownership review failed; see the service log."));
        }
    }
}

/// <summary>
///     Report of <c>POST system/v1/tenants/review-ownership</c> (AB#8796).
/// </summary>
public class TenantOwnershipReviewReportDto
{
    /// <summary>The system database (identity) of the instance that ran the review.</summary>
    public required string SystemDatabaseName { get; init; }

    /// <summary>True when the review ran with stamp.</summary>
    public bool Stamp { get; init; }

    /// <summary>Tenant databases checked.</summary>
    public int Checked { get; init; }

    /// <summary>Databases owned unambiguously by this instance (not listed).</summary>
    public int Owned { get; init; }

    /// <summary>Registry rows whose database does not exist (skipped).</summary>
    public int MissingDatabases { get; init; }

    /// <summary>The listed databases.</summary>
    public required IReadOnlyList<TenantOwnershipReviewEntryDto> Entries { get; init; }

    /// <summary>One-line summary.</summary>
    public required string Summary { get; init; }

    internal static TenantOwnershipReviewReportDto From(TenantOwnershipReviewResult result) => new()
    {
        SystemDatabaseName = result.SystemDatabaseName,
        Stamp = result.Stamp,
        Checked = result.Checked,
        Owned = result.Owned,
        MissingDatabases = result.MissingDatabases,
        Entries = result.Entries.Select(e => new TenantOwnershipReviewEntryDto
        {
            TenantId = e.TenantId,
            DatabaseName = e.DatabaseName,
            Issue = e.Issue.ToString(),
            MarkerOwnerSystemDatabaseName = e.MarkerOwnerSystemDatabaseName,
            RegisteringSystemDatabases = e.RegisteringSystemDatabases,
            Stamped = e.Stamped
        }).ToList(),
        Summary = result.Summary
    };
}

/// <summary>A tenant database listed by the ownership review.</summary>
public class TenantOwnershipReviewEntryDto
{
    /// <summary>Tenant id in this instance.</summary>
    public required string TenantId { get; init; }

    /// <summary>Tenant database name.</summary>
    public required string DatabaseName { get; init; }

    /// <summary><c>Unmarked</c>, <c>MarkedByOtherInstance</c> or <c>RegisteredByOtherInstance</c>.</summary>
    public required string Issue { get; init; }

    /// <summary>The system database the ownership marker names, or null without marker.</summary>
    public string? MarkerOwnerSystemDatabaseName { get; init; }

    /// <summary>All system databases on the server that register the database.</summary>
    public required IReadOnlyList<string> RegisteringSystemDatabases { get; init; }

    /// <summary>True when this call wrote the marker of this instance.</summary>
    public bool Stamped { get; init; }
}

/// <summary>
///     Report of <c>POST system/v1/tenants/reconcile-registry</c>.
/// </summary>
public class TenantRegistryReconcileReportDto
{
    /// <summary>True when the missing rows were written; false for a dry run.</summary>
    public bool Applied { get; init; }

    /// <summary>Tenants walked below the system tenant.</summary>
    public int Walked { get; init; }

    /// <summary>Rows inserted (apply) or that would be inserted (dry run).</summary>
    public required IReadOnlyList<TenantRegistryReconcileInsertDto> Inserted { get; init; }

    /// <summary>Tenants whose system registry row already matches.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>Rows left untouched, with the reason.</summary>
    public required IReadOnlyList<TenantRegistryReconcileConflictDto> Conflicts { get; init; }

    /// <summary>One-line summary.</summary>
    public required string Summary { get; init; }

    internal static TenantRegistryReconcileReportDto From(TenantRegistryReconcileResult result) => new()
    {
        Applied = result.Applied,
        Walked = result.Visited,
        Inserted = result.Inserted.Select(e => new TenantRegistryReconcileInsertDto
        {
            TenantId = e.TenantId, DatabaseName = e.DatabaseName, ParentTenantId = e.ParentTenantId
        }).ToList(),
        AlreadyPresent = result.AlreadyPresent,
        Conflicts = result.Conflicts.Select(c => new TenantRegistryReconcileConflictDto
        {
            TenantId = c.TenantId, ParentTenantId = c.ParentTenantId, Reason = c.Reason
        }).ToList(),
        Summary = result.Summary
    };
}

/// <summary>A system registry row inserted by the reconcile.</summary>
public class TenantRegistryReconcileInsertDto
{
    /// <summary>Tenant id.</summary>
    public required string TenantId { get; init; }

    /// <summary>Database name.</summary>
    public required string DatabaseName { get; init; }

    /// <summary>The tenant whose registry lists this tenant.</summary>
    public required string ParentTenantId { get; init; }
}

/// <summary>A tenant the reconcile left untouched.</summary>
public class TenantRegistryReconcileConflictDto
{
    /// <summary>Tenant id.</summary>
    public required string TenantId { get; init; }

    /// <summary>The tenant whose registry lists this tenant.</summary>
    public required string ParentTenantId { get; init; }

    /// <summary>Why the row was left alone.</summary>
    public required string Reason { get; init; }
}
