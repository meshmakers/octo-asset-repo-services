using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-scoped child tenant management.
///     Each tenant can manage its own child tenants through this API.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class TenantsController : ControllerBase
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ILogger<TenantsController> _logger;
    private readonly IOctoService _octoService;
    private readonly ITenantLifecycleStore _tenantLifecycleStore;

    /// <summary>
    ///     Constructor
    /// </summary>
    public TenantsController(IOctoService octoService, IDistributionEventHubService distributionEventHubService,
        ITenantLifecycleStore tenantLifecycleStore, ILogger<TenantsController> logger)
    {
        _octoService = octoService;
        _distributionEventHubService = distributionEventHubService;
        _tenantLifecycleStore = tenantLifecycleStore;
        _logger = logger;
    }

    private async Task<ITenantContext?> GetTenantContextAsync()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return null;
        }

        return await _octoService.SystemContext.TryFindTenantContextAsync(tenantId);
    }

    // GET {tenantId}/v1/tenants
    /// <summary>
    ///     Returns all child tenants of the current tenant. Supplying paging parameters returns a
    ///     <see cref="PagedResult{T}" /> plus an <c>X-Pagination</c> header; omitting them returns the
    ///     plain list.
    /// </summary>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<TenantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] PagingParams? pagingParams)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            var result = await tenantContext.GetChildTenantsAsync(session, pagingParams?.Skip, pagingParams?.Take);

            if (pagingParams != null)
            {
                var pagedResult = new PagedResult<TenantDto>(result.Items.Select(CreateTenantDto),
                    pagingParams.Skip, pagingParams.Take, result.TotalCount);

                Response.Headers.Append("X-Pagination", pagedResult.GetHeader().ToJson());

                await session.CommitTransactionAsync();

                return Ok(pagedResult);
            }

            await session.CommitTransactionAsync();

            return Ok(result.Items.Select(CreateTenantDto));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET {tenantId}/v1/tenants/self
    /// <summary>
    ///     Returns the current (own) tenant of the request, including its database name.
    /// </summary>
    /// <remarks>
    ///     A tenant's own <see cref="TenantDto.Database" /> is only resolvable server-side: the tenant
    ///     registry entry describing it lives in its parent's database (and in the system database),
    ///     never in its own, so neither the tenants list nor the runtime GraphQL surface of this tenant
    ///     can supply it. This endpoint exists so a tenant owner can back up and restore the tenant they
    ///     are currently in without access to the parent tenant.
    /// </remarks>
    [HttpGet("self")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSelf()
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var ownTenant = new TenantDto
            {
                TenantId = tenantContext.TenantId,
                Database = tenantContext.DatabaseName
            };

            return Ok(ownTenant);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET {tenantId}/v1/tenants/{id}
    /// <summary>
    ///     Returns a child tenant by its tenant ID
    /// </summary>
    /// <param name="id">ID of the child tenant</param>
    [HttpGet("{id}")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([Required] string id)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            if (!await tenantContext.IsChildTenantExistingAsync(session, id))
            {
                return NotFound();
            }

            var octoTenant = await tenantContext.GetChildTenantAsync(session, id);
            await session.CommitTransactionAsync();
            return Ok(CreateTenantDto(octoTenant));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/tenants?tenantId=abc&databaseName=xyz&blueprintId=MyBlueprint-1.0.0
    /// <summary>
    ///     Creates a new child tenant, optionally with a blueprint applied
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant to create</param>
    /// <param name="databaseName">Name of the database</param>
    /// <param name="blueprintId">Optional blueprint ID to apply</param>
    [HttpPost]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Post(
        [Required] string childTenantId,
        [Required] string databaseName,
        string? blueprintId = null)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // Serialize against an in-flight deletion: if the lifecycle store still records this tenant as
            // Deleting, its database drop has not finished yet. Surface a 409 instead of letting the
            // create proceed and fail later on "database already exists" (AB#4348 Phase 3).
            // The body is the engine's generic tenant-id conflict, reused verbatim so the two cannot
            // drift apart: the lifecycle store is platform-global and this endpoint takes any tenant id,
            // so a distinguishable "deletion in progress" answer would let any caller probe the state of
            // tenants they cannot see (AB#4763). The real state is logged instead.
            var normalizedTenantId = childTenantId.NormalizeString();
            var existingLifecycle = await _tenantLifecycleStore.GetAsync(normalizedTenantId);
            if (existingLifecycle is { State: TenantLifecycleState.Deleting })
            {
                _logger.LogWarning(
                    "Rejected creation of tenant '{TenantId}' because its deletion is still in progress. " +
                    "The caller only sees a generic conflict message.", normalizedTenantId);

                return Conflict(new OperationFailedErrorDto(
                    TenantException.TenantIdNotAvailable(childTenantId).Message));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            try
            {
                if (!string.IsNullOrEmpty(blueprintId))
                {
                    var bpId = new BlueprintId(blueprintId);
                    var result = await tenantContext.CreateChildTenantAsync(session, databaseName, childTenantId, bpId);

                    if (result != null && !result.IsSuccess)
                    {
                        await session.AbortTransactionAsync();
                        var messages = result.OperationResult?.Messages?.Select(m => m.MessageText) ?? [];
                        return BadRequest(new OperationFailedErrorDto(
                            $"Blueprint application failed: {string.Join(", ", messages)}"));
                    }
                }
                else
                {
                    await tenantContext.CreateChildTenantAsync(session, databaseName, childTenantId);
                }

                await session.CommitTransactionAsync();
            }
            catch
            {
                // Abort so the octosystem tenant entries inserted in this transaction are rolled
                // back (AB#1958). The engine rolls back the tenant database/user only when it
                // created them itself, and writes the failure to the event log — a create rejected
                // because the name was already taken leaves the existing database untouched
                // (AB#4762).
                try
                {
                    await session.AbortTransactionAsync();
                }
                catch
                {
                    // The driver may have already aborted the transaction - ignore.
                }

                throw;
            }

            return NoContent();
        }
        catch (TenantException e) when (e.IsConflict)
        {
            // Tenant or its database already exists. For the database case this is often a previous
            // deletion still completing its async drop (or an orphaned database) rather than a genuine
            // clash — surface it as a retryable 409 with an actionable message instead of a 400 that
            // reads like a permanent name conflict (AB#4348).
            return Conflict(new OperationFailedErrorDto(e.Message));
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/tenants/attach?childTenantId=abc&databaseName=xyz
    /// <summary>
    ///     Attaches an existing database as a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    /// <param name="databaseName">Name of the database (must exist)</param>
    [HttpPost("attach")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Attach([Required] string childTenantId, [Required] string databaseName)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.AttachChildTenantAsync(session, databaseName, childTenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (TenantException e) when (e.IsConflict)
        {
            // Same mapping as Post: attach shares both namespaces with create, so an identical
            // condition must not answer with a different status code (AB#4763).
            return Conflict(new OperationFailedErrorDto(e.Message));
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/tenants/detach?childTenantId=abc
    /// <summary>
    ///     Detaches a child tenant (keeps the database)
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPost("detach")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Detach([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.DetachChildTenantAsync(session, childTenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // PUT: {tenantId}/v1/tenants/clear?childTenantId=abc
    /// <summary>
    ///     Clears the content of a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPut("clear")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Clear([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.ClearChildTenantAsync(session, childTenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // PUT: {tenantId}/v1/tenants/clearCache?childTenantId=abc
    /// <summary>
    ///     Clears the caches of a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPut("clearCache")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearCache([Required] string childTenantId)
    {
        try
        {
            var correlationId = Guid.NewGuid();
            await _distributionEventHubService.PublishAsync(
                new PreUpdateTenant(childTenantId, correlationId, DateTime.Now));
            await Task.Delay(2000);
            await _distributionEventHubService.PublishAsync(
                new PosUpdateTenant(childTenantId, correlationId, DateTime.Now));

            return Ok("Cache cleared");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // DELETE: {tenantId}/v1/tenants?childTenantId=abc
    /// <summary>
    ///     Deletes a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpDelete]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Delete([Required] string childTenantId)
    {
        var normalizedTenantId = childTenantId.NormalizeString();
        var markedDeleting = false;
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // Q2: refuse to delete a tenant that is still being created. The reconciler drives a stalled
            // Creating tenant to Active or Failed, at which point the operator can retry the delete
            // (AB#4348 Phase 3).
            var lifecycle = await _tenantLifecycleStore.GetAsync(normalizedTenantId);
            if (lifecycle is { State: TenantLifecycleState.Creating })
            {
                // Generic for the same reason as the create-side guard: the lifecycle store is
                // platform-global and this endpoint accepts any tenant id, so a distinguishable answer
                // would expose the provisioning state of tenants outside the caller's subtree (AB#4763).
                _logger.LogWarning(
                    "Rejected deletion of tenant '{TenantId}' because it is still being created. " +
                    "The caller only sees a generic conflict message.", normalizedTenantId);

                return Conflict(new OperationFailedErrorDto(
                    TenantException.TenantIdNotAvailable(childTenantId).Message));
            }

            // Mark the tenant as being deleted (durable tombstone) BEFORE dropping its database, so a
            // concurrent Create serializes against it and returns a retryable 409 instead of racing the
            // async drop (AB#4348 Phase 3).
            await _tenantLifecycleStore.MarkDeletingAsync(normalizedTenantId);
            markedDeleting = true;

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            // Two-phase delete: remove the tenant metadata records first and COMMIT them, then drop
            // the physical database. Dropping the database while the tenant record is still visible
            // to other sessions leaves a window in which a concurrent tenant-resolve re-creates the
            // database via CK-model auto-import (it still finds the committed record), resurrecting
            // the just-dropped database and poisoning an immediately following tenant Create
            // (e.g. re-running om_initialize_tenant). Committing the record deletion first makes the
            // subsequent resolve fail with "tenant does not exist", so the drop is final.
            var deletion = await tenantContext.DeleteChildTenantMetadataAsync(session, childTenantId);
            await session.CommitTransactionAsync();
            await tenantContext.DropTenantDatabaseAsync(deletion, childTenantId);

            // The database drop has completed → remove the tombstone so the tenant id can be re-created
            // cleanly (AB#4348 Phase 3).
            await _tenantLifecycleStore.RemoveAsync(normalizedTenantId);
            return Ok();
        }
        catch (TenantException e)
        {
            await ClearDeletingTombstoneOnFailureAsync(normalizedTenantId, markedDeleting);
            return NotFound(e.Message);
        }
        catch (Exception ex)
        {
            await ClearDeletingTombstoneOnFailureAsync(normalizedTenantId, markedDeleting);
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    ///     If a delete fails after the Deleting tombstone was written, remove it so a re-create is not
    ///     blocked forever by the Create-side 409 guard. Correctness is still protected by the tenant /
    ///     database-exists checks the retried create runs (AB#4348 Phase 3).
    ///     Note that since AB#4762 those checks only reject the re-create — they no longer drop a
    ///     leftover database, so a delete that failed after committing its metadata removal leaves an
    ///     orphaned database that an operator has to reclaim (attach it, or drop it) before the tenant
    ///     id and database name become usable again. The engine logs both names when it rejects.
    /// </summary>
    private async Task ClearDeletingTombstoneOnFailureAsync(string normalizedTenantId, bool markedDeleting)
    {
        if (!markedDeleting)
        {
            return;
        }

        try
        {
            await _tenantLifecycleStore.RemoveAsync(normalizedTenantId);
        }
        catch
        {
            // Best-effort — a lingering tombstone is preferable to masking the original delete failure.
        }
    }

    // GET: {tenantId}/v1/tenants/lifecycle?childTenantId=abc
    /// <summary>
    ///     Returns the durable provisioning lifecycle state of a child tenant, or 404 when the tenant is
    ///     not a child of the current tenant or has no lifecycle record (e.g. a legacy tenant created
    ///     before lifecycle tracking) — AB#4348.
    /// </summary>
    /// <remarks>
    ///     The child check is what keeps the generic conflict messages on create and delete meaningful:
    ///     the lifecycle store is platform-global, so without it a caller could take the deliberately
    ///     reason-free "Tenant ID is already in use" and then read the colliding tenant's database name,
    ///     last error and lease owner from here (AB#4763). Mirrors the check in <see cref="Get(string)" />.
    /// </remarks>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpGet("lifecycle")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TenantLifecycleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLifecycle([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using (var session = await tenantContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var isChild = await tenantContext.IsChildTenantExistingAsync(session, childTenantId);
                await session.CommitTransactionAsync();

                if (!isChild)
                {
                    return NotFound();
                }
            }

            var record = await _tenantLifecycleStore.GetAsync(childTenantId.NormalizeString());
            if (record == null)
            {
                return NotFound();
            }

            return Ok(CreateLifecycleDto(record));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/tenants/rerunSetup?childTenantId=abc
    /// <summary>
    ///     Operator safety valve: re-opens a tenant's provisioning (resets it to Creating, clears the
    ///     attempt budget / last error / lease) so the background reconciler drives it to completion.
    ///     Returns the updated lifecycle state, or 404 when the tenant has no lifecycle record (AB#4348).
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPost("rerunSetup")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TenantLifecycleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReRunSetup([Required] string childTenantId)
    {
        try
        {
            var record = await _tenantLifecycleStore.RequeueForReconcileAsync(childTenantId.NormalizeString());
            if (record == null)
            {
                return NotFound();
            }

            return Ok(CreateLifecycleDto(record));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    private static TenantLifecycleDto CreateLifecycleDto(TenantLifecycleRecord record)
    {
        return new TenantLifecycleDto
        {
            TenantId = record.TenantId,
            DatabaseName = record.DatabaseName,
            State = record.State.ToString(),
            Phase = record.Phase.ToString(),
            AttemptCount = record.AttemptCount,
            LastError = record.LastError,
            CreatedUtc = record.CreatedUtc,
            LastTransitionUtc = record.LastTransitionUtc,
            LeaseOwner = record.LeaseOwner,
            LeaseUntil = record.LeaseUntil
        };
    }

    private static TenantDto CreateTenantDto(OctoTenant octoTenant)
    {
        return new TenantDto
        {
            TenantId = octoTenant.TenantId,
            Database = octoTenant.DatabaseName
        };
    }
}
