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
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    private readonly ITenantSetupRetryStore _tenantSetupRetryStore;
    private readonly ITenantCapabilityStateReader _capabilityStateReader;

    /// <summary>
    ///     Constructor
    /// </summary>
    public TenantsController(IOctoService octoService, IDistributionEventHubService distributionEventHubService,
        ITenantLifecycleStore tenantLifecycleStore, ITenantSetupRetryStore tenantSetupRetryStore,
        ILogger<TenantsController> logger, ITenantCapabilityStateReader capabilityStateReader)
    {
        _octoService = octoService;
        _distributionEventHubService = distributionEventHubService;
        _tenantLifecycleStore = tenantLifecycleStore;
        _tenantSetupRetryStore = tenantSetupRetryStore;
        _logger = logger;
        _capabilityStateReader = capabilityStateReader;
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

    /// <summary>
    ///     Refuses to delete or detach a child tenant while Stream Data, Communication, Reporting or AI
    ///     Services is still enabled for it (AB#4255). Those capabilities own state outside the tenant
    ///     database — archives, adapters and pools, report storage, AI configuration — that the plain
    ///     metadata delete/detach would orphan. Returns the 409 to send, or null to proceed.
    /// </summary>
    /// <remarks>
    ///     Ordering matters. The read must run AFTER the ownership probe: it resolves the child and reads
    ///     its database, which for a tenant outside the caller's subtree would be an existence oracle
    ///     (AB#4763). On delete it must also run AFTER the Creating guard (resolving a half-built tenant
    ///     runs the CK auto-imports on it) and BEFORE the Deleting tombstone is written — a refused delete
    ///     must not leave a tombstone that blocks the tenant id for the settle window (AB#4829). A child
    ///     that vanished since the probe surfaces as <see cref="TenantException" /> with
    ///     <c>IsTenantNotFound</c>; any other read failure propagates, because an unreadable state is
    ///     never "disabled".
    /// </remarks>
    private async Task<IActionResult?> RefuseWhileCapabilitiesEnabledAsync(ITenantContext tenantContext,
        string childTenantId, string operation, string operationPastTense)
    {
        var enabled = await _capabilityStateReader.GetEnabledCapabilitiesAsync(tenantContext, childTenantId);
        if (enabled.Count == 0)
        {
            return null;
        }

        _logger.LogWarning("Rejected {Operation} of tenant '{TenantId}': capabilities still enabled: {Capabilities}",
            operation, childTenantId, string.Join(", ", enabled));

        return Conflict(new OperationFailedErrorDto(
            BuildCapabilityConflictMessage(childTenantId, operationPastTense, enabled)));
    }

    /// <summary>
    ///     Builds the message of the 409 a delete/detach answers while capabilities are still enabled.
    ///     Single line, ASCII only: the CLI prints the raw JSON body. Names only the enabled capabilities
    ///     and only their disable verbs; the octo-cli disable verbs act on the tenant of the active
    ///     context (there is no tenant argument), and the Studio has no toggle for AI Services.
    /// </summary>
    internal static string BuildCapabilityConflictMessage(string childTenantId, string operationPastTense,
        IReadOnlyList<TenantCapability> enabledCapabilities)
    {
        var names = string.Join(", ", enabledCapabilities.Select(c => c.DisplayName()));
        var verbs = string.Join(", ", enabledCapabilities.Select(GetDisableCommandName));
        var studioHint = enabledCapabilities.Any(c => c != TenantCapability.AiServices)
            ? $", or use Refinery Studio (General > Settings > Tenant Features) of tenant '{childTenantId}'"
            : string.Empty;

        return $"Tenant '{childTenantId}' cannot be {operationPastTense} while the following capabilities are " +
               $"still enabled: {names}. Disable them on tenant '{childTenantId}' first: run {verbs} with octo-cli " +
               $"in a context of that tenant (UseContext or --context){studioHint}. " +
               "If the tenant's data is still needed, create a backup with Dump before disabling.";
    }

    private static string GetDisableCommandName(TenantCapability capability)
    {
        return capability switch
        {
            TenantCapability.StreamData => "DisableStreamData",
            TenantCapability.Communication => "DisableCommunication",
            TenantCapability.Reporting => "DisableReporting",
            TenantCapability.AiServices => "DisableAi",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
        };
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

            // Same Deleting guard as Post (AB#4829): during the delete's settle window the tenant id
            // is tombstoned. An attach that slipped in here registered a tenant whose live tombstone
            // made every service's setup skip silently — and nothing requeued that setup once the
            // sweep later removed the tombstone. The reply reuses the engine's generic tenant-id
            // conflict for the same no-existence-oracle reason as Post (AB#4763).
            var normalizedChildTenantId = childTenantId.NormalizeString();
            var attachLifecycle = await _tenantLifecycleStore.GetAsync(normalizedChildTenantId);
            if (attachLifecycle is { State: TenantLifecycleState.Deleting })
            {
                _logger.LogWarning(
                    "Rejected attach of tenant '{TenantId}' because its deletion is still settling. " +
                    "The caller only sees a generic conflict message.", normalizedChildTenantId);

                return Conflict(new OperationFailedErrorDto(
                    TenantException.TenantIdNotAvailable(childTenantId).Message));
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
        catch (ArgumentException e)
        {
            // The namespace gate rejects a format-invalid tenant id or database name with an
            // ArgumentException before any conflict check. Same mapping as Post — without this
            // branch the identical invalid input answered 400 on create but 500 on attach.
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
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

            // Same ownership probe as Delete (AB#4763): a tenant outside the caller's subtree answers a
            // reason-free 404. Before AB#4255 the engine's "does not exist" surfaced here as a 400 naming
            // the tenant, and the capability read below must never run for a foreign tenant.
            using (var probeSession = await tenantContext.GetAdminSessionAsync())
            {
                probeSession.StartTransaction();
                var isChild = await tenantContext.IsChildTenantExistingAsync(probeSession, childTenantId);
                await probeSession.CommitTransactionAsync();

                if (!isChild)
                {
                    return NotFound();
                }
            }

            // AB#4255: a detached tenant keeps its database but loses its registry entry, so adapters,
            // pools and archives it still owns would be orphaned exactly as on delete.
            var capabilityConflict =
                await RefuseWhileCapabilitiesEnabledAsync(tenantContext, childTenantId, "detach", "detached");
            if (capabilityConflict != null)
            {
                return capabilityConflict;
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.DetachChildTenantAsync(session, childTenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (TenantException e) when (e.IsConflict)
        {
            // TenantException derives from PersistenceException, so the branch below used to swallow a
            // conflict into a 400 — the same defect Attach had (AB#4763).
            return Conflict(new OperationFailedErrorDto(e.Message));
        }
        catch (TenantException e) when (e.IsTenantNotFound)
        {
            // The child vanished between the ownership probe and the detach (concurrent delete).
            return NotFound();
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
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Delete([Required] string childTenantId)
    {
        var normalizedTenantId = childTenantId.NormalizeString();
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // Establish that the tenant is ours BEFORE consulting the platform-global lifecycle store.
            // That store knows every tenant on the platform, so reading it first and answering on its
            // contents leaked the provisioning state of tenants outside the caller's subtree — and it
            // also forced the reply to be a generic "already in use", which reads as nonsense in
            // response to a delete. With the ownership settled first, the guard below can say what it
            // actually means (AB#4763).
            using (var probeSession = await tenantContext.GetAdminSessionAsync())
            {
                probeSession.StartTransaction();
                var isChild = await tenantContext.IsChildTenantExistingAsync(probeSession, childTenantId);
                await probeSession.CommitTransactionAsync();

                if (!isChild)
                {
                    return NotFound();
                }
            }

            // Q2: refuse to delete a tenant that is still being created. The reconciler drives a stalled
            // Creating tenant to Active or Failed, at which point the operator can retry the delete
            // (AB#4348 Phase 3).
            var lifecycle = await _tenantLifecycleStore.GetAsync(normalizedTenantId);
            if (lifecycle is { State: TenantLifecycleState.Creating })
            {
                return Conflict(new OperationFailedErrorDto(
                    $"Tenant '{childTenantId}' is still being created. Retry the delete once it is active or failed."));
            }

            // Q3 (AB#4255): refuse while Stream Data, Communication, Reporting or AI Services is still
            // enabled. Ordered after the ownership probe and the Creating guard, and before the tombstone
            // below — see RefuseWhileCapabilitiesEnabledAsync for why each of the three matters.
            var capabilityConflict =
                await RefuseWhileCapabilitiesEnabledAsync(tenantContext, childTenantId, "delete", "deleted");
            if (capabilityConflict != null)
            {
                return capabilityConflict;
            }

            // Mark the tenant as being deleted (durable tombstone) BEFORE dropping its database, so a
            // concurrent Create serializes against it and returns a retryable 409 instead of racing the
            // async drop (AB#4348 Phase 3).
            await _tenantLifecycleStore.MarkDeletingAsync(normalizedTenantId);

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            // Two-phase delete: remove the tenant metadata records first and COMMIT them, then drop
            // the physical database. Dropping the database while the tenant record is still visible
            // to other sessions leaves a window in which a concurrent tenant-resolve re-creates the
            // database via CK-model auto-import (it still finds the committed record), resurrecting
            // the just-dropped database and poisoning an immediately following tenant Create
            // (e.g. re-running om_initialize_tenant). Committing the record deletion first makes the
            // subsequent resolve fail with "tenant does not exist", so the drop is final.
            // dropStreamData: this is the one caller that removes the tenant for good, so the CrateDB
            // tables of its archives go with the database (AB#4255). A restore over an existing tenant
            // only swaps the database and keeps them.
            var deletion = await tenantContext.DeleteChildTenantMetadataAsync(session, childTenantId,
                dropStreamData: true);
            await session.CommitTransactionAsync();
            await tenantContext.DropTenantDatabaseAsync(deletion, childTenantId);

            // Take the tenant's pending setup retries with it. Otherwise every service's retry loop
            // keeps calling SetupAsync for a tenant that no longer exists, and that setup re-creates
            // the database as an empty CkModel+SysLock shell seconds after the drop. Since AB#4762 the
            // create path no longer reclaims such a shell, so the leftover would permanently block its
            // own database name behind a deliberately reason-free conflict.
            // Ordered BEFORE the tombstone removal below: the tombstone is what blocks a re-create of
            // this tenant id, so removing it first would open a window in which a re-created tenant
            // still had the old tenant's retry entries driving setup against it.
            try
            {
                var removedRetries = await _tenantSetupRetryStore.ClearAllForTenantAsync(normalizedTenantId);
                if (removedRetries > 0)
                {
                    _logger.LogInformation(
                        "Removed {Count} pending setup-retry entries of deleted tenant '{TenantId}'",
                        removedRetries, normalizedTenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Deleted tenant '{TenantId}' but failed to clear its setup-retry entries; a background " +
                    "retry may re-create its database as an orphan", normalizedTenantId);
            }

            // The tombstone deliberately STAYS (AB#4829): events and setups already in flight across
            // the platform can re-seed retry rows and resurrect the just-dropped database as an empty
            // shell for up to the settle period, and since AB#4762 the create path never reclaims such
            // a leftover — it would permanently block its own database name. EnsureDeleting upserts the
            // tombstone (covering legacy tenants MarkDeleting skipped), records the database name the
            // sweep needs for a re-drop, and restamps the settle clock to start at the drop. The
            // reconciler's settle sweep then completes the delete (re-drop, retry-row clear, tombstone
            // removal) roughly 90–120 s later; until then a re-create of this tenant id answers a
            // retryable 409 via the Deleting guard above. Best-effort: the delete itself has already
            // happened, and MarkDeleting's tombstone still stands for the sweep.
            try
            {
                await _tenantLifecycleStore.EnsureDeletingAsync(normalizedTenantId, deletion.DatabaseName,
                    deletion.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Deleted tenant '{TenantId}' but failed to restamp its settle tombstone; the sweep " +
                    "completes with whatever the tombstone holds", normalizedTenantId);
            }

            return Ok();
        }
        catch (TenantException e)
        {
            // Any tombstone written above deliberately stays (AB#4829): the settle sweep arbitrates a
            // failed delete — it rolls the tombstone back when the tenant is still fully registered
            // (the delete died before its metadata commit) and completes the delete when the registry
            // entry is gone, including the re-drop of a half-deleted database. Until then a re-create
            // of the tenant id answers a retryable 409.
            return NotFound(e.Message);
        }
        catch (Exception ex)
        {
            // See the TenantException branch — the sweep arbitrates, the tombstone stays.
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
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
