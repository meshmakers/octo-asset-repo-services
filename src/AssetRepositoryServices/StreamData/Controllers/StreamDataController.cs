using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using GraphQL;
using Duende.IdentityModel;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST Controller for stream data management
/// </summary>

[Microsoft.AspNetCore.Authorization.Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/streamdata")]
[ApiVersion("1.0")]
public class StreamDataController : ControllerBase
{
    private readonly ILogger<StreamDataController> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IHostApplicationLifetime _appLifetime;

    /// <summary>
    /// Constructor
    /// </summary>
    public StreamDataController(
        ILogger<StreamDataController> logger,
        ISystemContext systemContext,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _systemContext = systemContext;
        _appLifetime = appLifetime;
    }

    // The former GET status endpoint moved to FeaturesController (GET {tenantId}/v1/features/status,
    // AB#4884), which reports all four tenant capabilities from the delete/detach guard's reader.

    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    [HttpPost("enable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAdministrationPolicy)]
    public async Task<IActionResult> Enable([Required] string tenantId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.EnableStreamDataAsync();
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            // Concept §12: known stream-data failures (e.g. instance-level disabled, archive
            // path invalid, activation failed) — return the message text only, no stack trace.
            _logger.LogWarning("EnableStreamData refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Activates a CkArchive: provisions the per-archive CrateDB table and transitions the archive
    /// to <c>Activated</c>. Allowed from <c>Created</c>, <c>Disabled</c>, or <c>Failed</c>;
    /// idempotent on <c>Activated</c>. Same lifecycle path as the <c>activateArchive</c> GraphQL
    /// mutation — exposed as REST so headless tooling (octo-cli, deployment scripts) can finish
    /// the rt-import → activate handshake without a GraphQL client.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/activate")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> ActivateArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "ActivateArchive",
            (lifecycle, id) => lifecycle.ActivateAsync(id));

    /// <summary>
    /// Disables stream data for a given tenant
    /// </summary>
    /// <remarks>
    ///     Verified precondition, not a teardown (AB#4255): the engine refuses while any archive of the
    ///     tenant is still Activated (<see cref="StreamDataDisableBlockedException" />), mapped here to
    ///     409 with an <see cref="OperationFailedErrorDto" /> that names the archives and the remediation
    ///     verbs. A successful disable only switches the tenant flag off; the System.StreamData model, the
    ///     archive entities and the tables of Disabled/Failed archives stay until the tenant is deleted,
    ///     which also drops the CrateDB tables of its archives.
    /// </remarks>
    [HttpPost("disable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAdministrationPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable([Required] string tenantId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.DisableStreamDataAsync();
            return NoContent();
        }
        catch (StreamDataDisableBlockedException e)
        {
            // The engine already logged the refusal (WARN); this line names the HTTP outcome.
            _logger.LogWarning("DisableStreamData answered 409 for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return Conflict(new OperationFailedErrorDto(BuildDisableBlockedMessage(e)));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("DisableStreamData refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    ///     The engine names the blocking archives; the operator-facing remediation verbs are added here,
    ///     where the surfaces (octo-cli, MCP, Studio) that call this endpoint are known.
    /// </summary>
    private static string BuildDisableBlockedMessage(StreamDataDisableBlockedException e) =>
        e.Message +
        " Use DisableArchive (data kept) or DeleteArchive - both act on the tenant of the active context - " +
        "or Refinery Studio > Repository > Archives, then retry DisableStreamData.";

    /// <summary>
    /// Disables a CkArchive: transitions to <c>Disabled</c> (no DDL side-effect; data preserved).
    /// Allowed only from <c>Activated</c>.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/disable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> DisableArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "DisableArchive",
            (lifecycle, id) => lifecycle.DisableAsync(id));

    /// <summary>
    /// Re-enables a previously disabled archive: transitions <c>Disabled → Activated</c>. Re-validates
    /// column paths against the current CK model; no DDL because the table already exists.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/enable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> EnableArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "EnableArchive",
            (lifecycle, id) => lifecycle.EnableAsync(id));

    /// <summary>
    /// Retries activation after a previous DDL failure. Allowed only from <c>Failed</c>.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/retry")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> RetryArchiveActivation([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "RetryArchiveActivation",
            (lifecycle, id) => lifecycle.RetryActivationAsync(id));

    /// <summary>
    /// Drops the per-archive CrateDB table (idempotent) and soft-deletes the <c>CkArchive</c>
    /// entity. Destructive — historical data is lost. Allowed from any status.
    /// </summary>
    [HttpDelete("archives/{archiveRtId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> DeleteArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "DeleteArchive",
            (lifecycle, id) => lifecycle.DeleteAsync(id));

    /// <summary>
    /// Freezes a rollup archive at <paramref name="until"/>. Monotonic — rejected when the new
    /// value is earlier than the current FrozenUntil. Rollup-archives concept §9.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/freeze")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> FreezeRollupArchive(
        [Required] string tenantId, [Required] string rollupRtId, [Required] DateTime until)
        => InvokeRollupAsync(tenantId, rollupRtId, "FreezeRollup",
            (lifecycle, id) => lifecycle.FreezeAsync(id, until));

    /// <summary>
    /// Clears FrozenUntil on the rollup archive. Idempotent. Concept §9.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/unfreeze")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> UnfreezeRollupArchive(
        [Required] string tenantId, [Required] string rollupRtId, bool acceptGaps = false)
        => InvokeRollupAsync(tenantId, rollupRtId, "UnfreezeRollup",
            (lifecycle, id) => lifecycle.UnfreezeAsync(id, acceptGaps));

    /// <summary>
    /// Resets the rollup's watermark (truncated down to the bucket boundary) so subsequent
    /// orchestrator ticks re-aggregate the rewound range. Destructive: rows in that range are
    /// temporarily out of sync until the orchestrator catches up. Concept §5, §9.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/rewind")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> RewindRollupWatermark(
        [Required] string tenantId, [Required] string rollupRtId, [Required] DateTime toBucketEnd)
        => InvokeRollupAsync(tenantId, rollupRtId, "RewindRollup",
            (lifecycle, id) => lifecycle.RewindWatermarkAsync(id, toBucketEnd));

    /// <summary>
    /// Adds a computed column to an Activated raw or time-range archive (AB#4189 Phase 7) and queues
    /// its backfill as a background job (AB#8002): returns at once with the column in
    /// <c>Backfilling</c>; the job writes the existing rows outside this request — a client timeout
    /// cannot cancel it — and ends the column <c>Active</c> or <c>Failed</c>. Follow it with the
    /// recompute-jobs endpoint of the archive. Same lifecycle path as the <c>addComputedColumn</c>
    /// GraphQL mutation.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/computed-columns")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> AddComputedColumn(
        [Required] string tenantId, [Required] string archiveRtId,
        [Required] string name, [Required] string formula, [Required] FormulaResultType resultType,
        bool indexed = true)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "AddComputedColumn",
            (lifecycle, id) => lifecycle.EnqueueAddComputedColumnAsync(
                id, name, formula, resultType, indexed, _appLifetime.ApplicationStopping));

    /// <summary>
    /// Adds an ingested column for an existing attribute of the archive's target CK type to an Activated raw or
    /// time-range archive without recreating it (AB#7997): the table gains a nullable column (existing rows keep their
    /// data and read NULL for it), then the archive definition names it, and the next ingested rows fill it. Idempotent:
    /// a path the archive already captures is a no-op. 400 when the archive is not Activated, is a rollup, or the path
    /// does not resolve. Same lifecycle path as the <c>addArchiveColumn</c> GraphQL mutation.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/columns")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> AddArchiveColumn(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string path, bool indexed = false)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "AddArchiveColumn",
            (lifecycle, id) => lifecycle.AddArchiveColumnAsync(id, path, indexed, _appLifetime.ApplicationStopping));

    /// <summary>
    /// Removes a computed column from an archive (AB#4189 Phase 7). Rejected when another computed
    /// column still references it; the physical CrateDB column is left as a harmless orphan.
    /// </summary>
    [HttpDelete("archives/{archiveRtId}/computed-columns/{name}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> RemoveComputedColumn(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string name)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "RemoveComputedColumn",
            (lifecycle, id) => lifecycle.RemoveComputedColumnAsync(id, name));

    /// <summary>
    /// Changes the formula of an existing computed column on an active archive with optimistic /
    /// atomic semantics (AB#4189 Phase 7): readers keep the previous values while the new formula is
    /// backfilled, then switch atomically. The backfill runs as a background job (AB#8002); the call
    /// returns once it is queued. Rejected when another computed column references this one, or
    /// while a backfill of the column is still queued or running.
    /// </summary>
    [HttpPut("archives/{archiveRtId}/computed-columns/{name}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> UpdateComputedColumnFormula(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string name,
        [Required] string formula)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "UpdateComputedColumnFormula",
            (lifecycle, id) => lifecycle.EnqueueUpdateComputedColumnFormulaAsync(
                id, name, formula, _appLifetime.ApplicationStopping));

    /// <summary>
    /// Queues the backfill of a computed column again (AB#8002) — for a column that ended
    /// <c>Failed</c>, or that is stuck in <c>Pending</c> / <c>Backfilling</c> without a running job —
    /// without removing and re-adding it. Returns the queued job; poll it via the recompute-jobs
    /// endpoint. 400 when the column is Active (nothing to retry) or does not exist.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/computed-columns/{name}/retry")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<ActionResult<RecomputeJobInfoRestDto>> RetryComputedColumnBackfill(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string name)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var lifecycle = tenantContext.GetArchiveLifecycleService()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");
            var job = await lifecycle.RetryComputedColumnBackfillAsync(
                new OctoObjectId(archiveRtId), name, _appLifetime.ApplicationStopping);
            return job is null ? NoContent() : Ok(RecomputeJobInfoRestDto.From(job));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (Exception e) when (e is StreamDataException or InvalidOperationException)
        {
            _logger.LogWarning("RetryComputedColumnBackfill refused for tenant '{TenantId}', archive '{ArchiveRtId}', column '{Column}': {Reason}",
                tenantId, archiveRtId, name, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Queues a repair of an Active computed column (AB#8331): a background job re-evaluates the rows in
    /// <c>[from, to)</c> whose cell is NULL although an operand the formula references is not NULL and writes
    /// the results; the column stays Active. <paramref name="from"/> defaults to the first row,
    /// <paramref name="to"/> to now. Returns the queued job (an already queued repair is returned as is); poll it
    /// via the recompute-jobs endpoint. 400 when the column is not Active or does not exist.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/computed-columns/{name}/repair")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<ActionResult<RecomputeJobInfoRestDto>> RepairComputedColumn(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string name,
        DateTime? from, DateTime? to)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var lifecycle = tenantContext.GetArchiveLifecycleService()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");
            var job = await lifecycle.RepairComputedColumnAsync(
                new OctoObjectId(archiveRtId), name, AsUtc(from), AsUtc(to),
                _appLifetime.ApplicationStopping);
            return job is null ? NoContent() : Ok(RecomputeJobInfoRestDto.From(job));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (Exception e) when (e is StreamDataException or InvalidOperationException)
        {
            _logger.LogWarning("RepairComputedColumn refused for tenant '{TenantId}', archive '{ArchiveRtId}', column '{Column}': {Reason}",
                tenantId, archiveRtId, name, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Triggers (or coalesces) an optimistic recompute of a rollup archive over the half-open range
    /// <c>[from, to)</c>, optionally scoped to a single entity. Returns the resulting job snapshot.
    /// AB#4184.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/recompute")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<ActionResult<RecomputeJobInfoRestDto>> RecomputeArchive(
        [Required] string tenantId, [Required] string rollupRtId,
        [Required] DateTime from, [Required] DateTime to, string? rtIdScope)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var orchestrator = tenantContext.GetRecomputeOrchestrator()
                ?? throw new StreamDataException(
                    $"Recompute support is not wired for tenant '{tenantId}'. Ensure stream data is enabled and a rollup store is registered.");

            // AB#4286: run under the host application-lifetime token, NOT HttpContext.RequestAborted —
            // a client HTTP timeout / disconnect must never cancel a server-side recompute (the octo-cli
            // HttpClient's 100s default previously killed long recomputes mid-flight). The job is
            // pollable via the recompute-jobs endpoint even if the client has already gone away.
            var job = await orchestrator.RecomputeArchiveAsync(
                new OctoObjectId(rollupRtId), from, to,
                rtIdScope is not null ? new OctoObjectId(rtIdScope) : null,
                RecomputeTrigger.Manual, _appLifetime.ApplicationStopping);

            return Ok(RecomputeJobInfoRestDto.From(job));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("Recompute refused for tenant '{TenantId}', rollup '{RollupRtId}': {Reason}",
                tenantId, rollupRtId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Queues a <em>durable, background</em> backfill that populates / resets a rollup over the ENTIRE
    /// history of its source archive without supplying a timestamp (AB#4269 / AB#4286). Resolves the
    /// source archive's earliest timestamp, enqueues a persisted pending recompute range
    /// <c>[sourceMin, now)</c> and a Pending <c>RecomputeJob</c>, and returns <em>immediately</em> with
    /// that job snapshot. The heavy recompute is executed later by the background recompute orchestrator
    /// under the host application-lifetime token — never bound to this HTTP request — so a client
    /// timeout / disconnect can no longer cancel a multi-minute (e.g. decade-long) backfill, and the
    /// queued work survives an asset-repo restart. Poll the returned job id via the recompute-jobs
    /// endpoint to observe Pending → Running → Completed. Returns 204 No Content when the source archive
    /// holds no data (no-op).
    /// </summary>
    [HttpPost("archives/{rollupRtId}/backfill-from-source")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<ActionResult<RecomputeJobInfoRestDto>> BackfillRollupFromSource(
        [Required] string tenantId, [Required] string rollupRtId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var orchestrator = tenantContext.GetRecomputeOrchestrator()
                ?? throw new StreamDataException(
                    $"Recompute support is not wired for tenant '{tenantId}'. Ensure stream data is enabled and a rollup store is registered.");

            // Enqueue + return the Pending job immediately. Use the app-lifetime token for the quick
            // resolve/enqueue writes (not HttpContext.RequestAborted) so even the enqueue is not tied to
            // the client connection; the recompute itself runs later on the background orchestrator tick.
            var job = await orchestrator.EnqueueBackfillFromSourceAsync(
                new OctoObjectId(rollupRtId), _appLifetime.ApplicationStopping);

            // Null = empty source archive (no-op): 204 so the SDK can return null.
            return job is null ? NoContent() : Ok(RecomputeJobInfoRestDto.From(job));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("Backfill refused for tenant '{TenantId}', rollup '{RollupRtId}': {Reason}",
                tenantId, rollupRtId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Checks rollup archives for the inconsistencies a restore (or a dump taken during a recompute) can leave:
    /// a pointer without rows, rows above the pointer, a watermark ahead of the rows, a missing generation map
    /// (AB#6968). Read only; the repository runs counts and MAX/MIN statements, nothing is changed. An inconsistent
    /// rollup is reported in the body, not as an error. Without <c>rollupRtId</c> every rollup of the tenant is checked.
    /// </summary>
    [HttpGet("rollups/validate")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<RollupConsistencyReportRestDto>> ValidateRollups(
        [Required] string tenantId, [FromQuery] string[]? rollupRtId)
    {
        if (!TryParseRollupIds(rollupRtId, out var rollupRtIds, out var invalidId))
        {
            return BadRequest($"'{invalidId}' is not a valid rollup runtime id.");
        }

        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            var report = await repository.ValidateRollupsAsync(rollupRtIds, HttpContext.RequestAborted);
            return Ok(RollupConsistencyReportRestDto.From(report));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (ArchiveCopyNotSupportedException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("ValidateRollups refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Repairs what <c>rollups/validate</c> finds (AB#6968). Idempotent. With <c>dryRun=true</c> (the default) the
    /// planned steps per rollup are listed and nothing is changed. With <c>dryRun=false</c> the repair is only
    /// <em>requested</em>: the call answers 202 with the request (and a <c>Location</c> to
    /// <c>rollups/repair/{requestId}</c>) and the host executes it in the background, so a large recompute is never
    /// bound to this HTTP request (an ingress cuts a request long before it ends). A second request while one is
    /// active returns the active one. Without <c>rollupRtId</c> every rollup of the tenant is repaired.
    /// </summary>
    [HttpPost("rollups/repair")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<IActionResult> RepairRollups(
        [Required] string tenantId, [FromQuery] string[]? rollupRtId, [FromQuery] bool dryRun = true)
    {
        if (!TryParseRollupIds(rollupRtId, out var rollupRtIds, out var invalidId))
        {
            return BadRequest($"'{invalidId}' is not a valid rollup runtime id.");
        }

        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            if (dryRun)
            {
                var plan = await repository.RepairRollupsAsync(rollupRtIds, true, HttpContext.RequestAborted);
                return Ok(RollupRepairReportRestDto.From(plan));
            }

            // Under the application-lifetime token like the recompute endpoints (AB#4286): the enqueue is a quick
            // write that must not depend on the client connection; the repair itself runs on the host tick.
            var request = await repository.EnqueueRollupRepairAsync(rollupRtIds, _appLifetime.ApplicationStopping);
            var location = Url?.Action(nameof(GetRollupRepair),
                new { tenantId, requestId = request.RequestId }) ?? $"rollups/repair/{request.RequestId}";
            return Accepted(location, RollupRepairRequestRestDto.From(request));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (ArchiveCopyNotSupportedException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("RepairRollups refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// The state of a rollup repair request (AB#6968): <c>Pending</c>, <c>Running</c>, <c>Done</c> or <c>Failed</c>,
    /// with the final report once it has ended. 404 for an unknown request id.
    /// </summary>
    [HttpGet("rollups/repair/{requestId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<RollupRepairRequestRestDto>> GetRollupRepair(
        [Required] string tenantId, [Required] string requestId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            var request = await repository.GetRollupRepairRequestAsync(requestId, HttpContext.RequestAborted);
            return request is null
                ? NotFound($"Rollup repair request '{requestId}' does not exist.")
                : Ok(RollupRepairRequestRestDto.From(request));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (ArchiveCopyNotSupportedException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("GetRollupRepair refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    private static bool TryParseRollupIds(string[]? values, out IReadOnlyCollection<OctoObjectId>? ids,
        out string? invalidId)
    {
        ids = null;
        invalidId = null;
        if (values is null || values.Length == 0)
        {
            return true;
        }

        var parsed = new List<OctoObjectId>(values.Length);
        foreach (var value in values)
        {
            if (!OctoObjectId.TryParse(value, out var id))
            {
                invalidId = value;
                return false;
            }

            parsed.Add(id);
        }

        ids = parsed;
        return true;
    }

    /// <summary>
    /// Lists the most recent recompute jobs for a rollup archive (newest first, capped at 50) — for
    /// debugging why a recompute failed. AB#4184.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/recompute-jobs")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<IReadOnlyList<RecomputeJobInfoRestDto>>> ListRecomputeJobsForArchive(
        [Required] string tenantId, [Required] string archiveRtId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var jobStore = tenantContext.GetRecomputeJobStore();
            if (jobStore is null)
            {
                return Ok(Array.Empty<RecomputeJobInfoRestDto>());
            }

            var jobs = await jobStore.GetForArchiveAsync(new OctoObjectId(archiveRtId), 50);
            return Ok(jobs.Select(RecomputeJobInfoRestDto.From).ToList());
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Inserts or corrects data points of a raw archive (AB#8590). A raw row is identified by archive + <c>rtId</c> +
    /// <c>timestamp</c> (UTC, millisecond precision); the CK type is fixed per archive. A write with an existing identity
    /// replaces the stored row subject to the archive's conflict precedence: a manual row is never replaced by an
    /// automated delivery, and protection is per row (a manual correction of one attribute protects the whole row).
    /// Each point may carry <c>origin</c> (<c>manual</c> | <c>automated</c>, absent = automated) and, only with
    /// <c>manual</c>, a <c>reason</c>; the writer and the write time come from the verified caller and the platform,
    /// never from the body. A manual point must name every ingested column of the archive (explicit null allowed).
    /// Answers 200 with the write result, 204 for an empty batch, 409 with the result when a manual row could not be
    /// stored, and 400 naming the point and the field for every client error; nothing of a refused batch is written.
    /// A batch with a manual point needs the tenant role <c>StreamDataAdmin</c> besides the write scope, otherwise the
    /// whole batch answers 403 (AB#8590 AC 8); automated points need the scope only.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/insert")]
    [ProducesResponseType(typeof(StreamDataWriteResultRestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ForbiddenErrorDto), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ManualWriteNotStoredRestDto), StatusCodes.Status409Conflict)]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<IActionResult> Insert(
        [Required] string tenantId,
        [Required] string archiveRtId,
        [FromBody] IReadOnlyList<InsertRawPointRestDto> points)
    {
        try
        {
            if (points is null || points.Count == 0)
            {
                return NoContent();
            }

            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            var snapshot = await LoadArchiveSnapshotAsync(tenantContext, archiveRtId);
            if (snapshot is { UsesWindowedStorage: true })
            {
                return BadRequest(snapshot.IsTimeRange
                    ? $"Archive '{archiveRtId}' is a time-range archive. Write its rows with POST archives/{archiveRtId}/insertTimeRange."
                    : $"Archive '{archiveRtId}' is a rollup archive. Its rows are computed from the source archive and cannot be written.");
            }

            var mismatch = CkTypeMismatchMessage(archiveRtId, snapshot, points.Select(p => p.CkTypeId));
            if (mismatch is not null)
            {
                return BadRequest(mismatch);
            }

            var rtIds = new OctoObjectId[points.Count];
            var timestamps = new DateTime[points.Count];
            var origins = new ArchiveRowOrigin[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (!OctoObjectId.TryParse(point.RtId, out rtIds[i]))
                {
                    return BadRequest($"Point {i}: rtId '{point.RtId}' is not a valid runtime id.");
                }

                var timestampError = ArchivePointValidation.NormalizeTimestamp(i, point.Timestamp, out timestamps[i]);
                if (timestampError is not null)
                {
                    return BadRequest(timestampError);
                }

                if (!TryParseOrigin(point.Origin, out origins[i]))
                {
                    return BadRequest(
                        $"Point {i}: origin '{point.Origin}' is not valid. Use 'manual' or 'automated', or leave it out for automated.");
                }

                if (origins[i] != ArchiveRowOrigin.Manual && !string.IsNullOrEmpty(point.Reason))
                {
                    return BadRequest($"Point {i}: a reason is only recorded for a manual write; set origin 'manual' or remove the reason.");
                }

                if (snapshot is not null)
                {
                    var columnError = ArchivePointValidation.CheckColumns(
                        i, point.Attributes, snapshot, origins[i] == ArchiveRowOrigin.Manual);
                    if (columnError is not null)
                    {
                        return BadRequest(columnError);
                    }
                }
            }

            var refusal = RefuseManualWriteWithoutRole(tenantId, archiveRtId, origins);
            if (refusal is not null)
            {
                return refusal;
            }

            var writer = origins.Contains(ArchiveRowOrigin.Manual) ? CallerIdentity(User) : null;
            if (origins.Contains(ArchiveRowOrigin.Manual) && writer is null)
            {
                return BadRequest("A manual write records who wrote it, and the caller's token names no subject or client.");
            }

            var domainPoints = points.Select((p, i) => new StreamDataPoint
            {
                RtId = rtIds[i],
                CkTypeId = new RtCkId<CkTypeId>(p.CkTypeId),
                Timestamp = timestamps[i],
                RtWellKnownName = p.RtWellKnownName,
                Attributes = p.Attributes,
                Origin = origins[i],
                ManualWrittenBy = origins[i] == ArchiveRowOrigin.Manual ? writer : null,
                ManualReason = origins[i] == ArchiveRowOrigin.Manual ? p.Reason : null,
            }).ToList();

            var result = await repository.InsertAsync(new OctoObjectId(archiveRtId), domainPoints);
            return Ok(StreamDataWriteResultRestDto.From(result));
        }
        catch (ManualStreamDataNotAppliedException e)
        {
            // A manual value that could not be stored is never a success; the body says what was stored.
            _logger.LogWarning(
                "Insert for tenant '{TenantId}', archive '{ArchiveRtId}': {Skipped} manual row(s) not stored.",
                tenantId, archiveRtId, e.Result.Skipped.Count(s => s.Origin == ArchiveRowOrigin.Manual));
            return Conflict(new ManualWriteNotStoredRestDto
            {
                Message = e.Message,
                Result = StreamDataWriteResultRestDto.From(e.Result)
            });
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning(
                "Insert refused for tenant '{TenantId}', archive '{ArchiveRtId}': {Reason}",
                tenantId, archiveRtId, e.Message);
            return BadRequest(e.Message);
        }
        catch (ArgumentException e)
        {
            // The repository's value check (a value that does not fit its column type, AB#8606) and malformed ids.
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Inserts a batch of externally-aggregated time-range data points into a
    /// <c>TimeRangeArchive</c>. Each row covers a half-open <c>[from, to)</c> window;
    /// re-deliveries upsert via the natural key and set the row's <c>was_updated</c> flag to
    /// true. Time-range concept §3. The archive must be in <c>Activated</c> status; non-time-
    /// range archives reject the call with HTTP 400. Answers 200 with the write result (rows stored, rows not
    /// stored with the reason, AB#7598) — an empty batch still answers 204 — and 409 with the result when a
    /// manual row could not be stored. Every point is checked like the raw <c>insert</c> before anything is written, each
    /// a 400 naming the point and the field (AB#8590): missing or sub-millisecond <c>from</c>/<c>to</c>, invalid origin,
    /// reason without manual, ckTypeId mismatch, and an attribute that is no ingested column or a computed column (also
    /// for automated points). A manual point must name every ingested column of the archive (explicit null allowed),
    /// otherwise the batch answers 400 listing the missing columns (AB#8590 AC 9).
    /// A batch with a manual point needs the tenant role <c>StreamDataAdmin</c> besides the write scope, otherwise the
    /// whole batch answers 403 (AB#8590 AC 8).
    /// </summary>
    [HttpPost("archives/{archiveRtId}/insertTimeRange")]
    [ProducesResponseType(typeof(StreamDataWriteResultRestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ForbiddenErrorDto), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ManualWriteNotStoredRestDto), StatusCodes.Status409Conflict)]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public async Task<IActionResult> InsertTimeRange(
        [Required] string tenantId,
        [Required] string archiveRtId,
        [FromBody] IReadOnlyList<InsertTimeRangePointRestDto> points)
    {
        try
        {
            if (points is null || points.Count == 0)
            {
                return NoContent();
            }

            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            // A per-archive table holds exactly one CkType, so the repository drops every row whose
            // CkTypeId is not the archive's target. That is the right behaviour for the pipeline's
            // multi-archive batches, but on this single-archive endpoint every such row is a caller
            // mistake — and the drop is silent, so the call answered 204 with nothing written. The
            // usual cause is sending the VERSIONED id ('Model-1.0.0/Type-1') where an RtCkId is
            // expected ('Model/Type'): it parses, it just never matches. Refuse the batch here and
            // name both sides (AB#5157 validation finding 4).
            var snapshot = await LoadArchiveSnapshotAsync(tenantContext, archiveRtId);
            var mismatch = CkTypeMismatchMessage(archiveRtId, snapshot, points.Select(p => p.CkTypeId));
            if (mismatch is not null)
            {
                return BadRequest(mismatch);
            }

            // AB#5356 / AB#7593 / AB#7596: the caller declares the origin; the writer of a manual row is
            // the authenticated caller, never a value from the body.
            var origins = new ArchiveRowOrigin[points.Count];
            var froms = new DateTime[points.Count];
            var tos = new DateTime[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                // AB#8590 point 11: the window bounds are the row key, held in milliseconds, like the raw timestamp.
                var windowError = ArchivePointValidation.NormalizeTimestamp(i, points[i].From, out froms[i], "from")
                                  ?? ArchivePointValidation.NormalizeTimestamp(i, points[i].To, out tos[i], "to");
                if (windowError is not null)
                {
                    return BadRequest(windowError);
                }

                if (!TryParseOrigin(points[i].Origin, out origins[i]))
                {
                    return BadRequest(
                        $"Point {i}: origin '{points[i].Origin}' is not valid. Use 'manual' or 'automated', or leave it out for automated.");
                }

                if (origins[i] != ArchiveRowOrigin.Manual && !string.IsNullOrEmpty(points[i].Reason))
                {
                    return BadRequest($"Point {i}: a reason is only recorded for a manual write; set origin 'manual' or remove the reason.");
                }

                // AB#8590 point 11: the same column checks as the raw insert, for every point. An unknown or computed key
                // would be dropped by the repository without a word, so the caller would believe it was written. AC 9: the
                // upsert binds every ingested column, so a manual point must name each one (an unsent one would become NULL).
                if (snapshot is not null)
                {
                    var columnError = ArchivePointValidation.CheckColumns(
                        i, points[i].Attributes, snapshot, origins[i] == ArchiveRowOrigin.Manual);
                    if (columnError is not null)
                    {
                        return BadRequest(columnError);
                    }
                }
            }

            var refusal = RefuseManualWriteWithoutRole(tenantId, archiveRtId, origins);
            if (refusal is not null)
            {
                return refusal;
            }

            var writer = origins.Contains(ArchiveRowOrigin.Manual) ? CallerIdentity(User) : null;
            if (origins.Contains(ArchiveRowOrigin.Manual) && writer is null)
            {
                return BadRequest("A manual write records who wrote it, and the caller's token names no subject or client.");
            }

            var domainPoints = points.Select((p, i) => new TimeRangeStreamDataPoint
            {
                RtId = new OctoObjectId(p.RtId),
                CkTypeId = new RtCkId<CkTypeId>(p.CkTypeId),
                From = froms[i],
                To = tos[i],
                RtWellKnownName = p.RtWellKnownName,
                Attributes = p.Attributes,
                Origin = origins[i],
                ManualWrittenBy = origins[i] == ArchiveRowOrigin.Manual ? writer : null,
                ManualReason = origins[i] == ArchiveRowOrigin.Manual ? p.Reason : null,
            }).ToList();

            // AB#7598: the caller learns how many rows were stored and why the others were not.
            var result = await repository.InsertTimeRangeAsync(new OctoObjectId(archiveRtId), domainPoints);
            return Ok(StreamDataWriteResultRestDto.From(result));
        }
        catch (ManualStreamDataNotAppliedException e)
        {
            // A manual value that could not be stored is never a success; the body says what was stored.
            _logger.LogWarning(
                "InsertTimeRange for tenant '{TenantId}', archive '{ArchiveRtId}': {Skipped} manual row(s) not stored.",
                tenantId, archiveRtId, e.Result.Skipped.Count(s => s.Origin == ArchiveRowOrigin.Manual));
            return Conflict(new ManualWriteNotStoredRestDto
            {
                Message = e.Message,
                Result = StreamDataWriteResultRestDto.From(e.Result)
            });
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning(
                "InsertTimeRange refused for tenant '{TenantId}', archive '{ArchiveRtId}': {Reason}",
                tenantId, archiveRtId, e.Message);
            return BadRequest(e.Message);
        }
        catch (ArgumentException e)
        {
            // Covers the To <= From validation and the non-time-range archive guard in
            // CrateDbStreamDataRepository.InsertTimeRangeAsync.
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// AB#8590 AC 8: a manual point corrects data by hand, so besides the write scope of the endpoint policy the verified
    /// caller must hold the tenant role <c>StreamDataAdmin</c> (the role Studio checks for corrections). A batch with at
    /// least one manual point from a caller without the role is refused as a whole with 403 before anything is written;
    /// a purely automated batch is not checked here. Returns <c>null</c> when the batch may be written.
    /// </summary>
    private ObjectResult? RefuseManualWriteWithoutRole(string tenantId, string archiveRtId, ArchiveRowOrigin[] origins)
    {
        if (!origins.Contains(ArchiveRowOrigin.Manual) ||
            TenantRoleAuthorizationHandler.HasTenantRole(User, CommonConstants.StreamDataAdminRole))
        {
            return null;
        }

        _logger.LogWarning(
            "Manual stream-data write refused for tenant '{TenantId}', archive '{ArchiveRtId}': the caller lacks the role '{Role}'.",
            tenantId, archiveRtId, CommonConstants.StreamDataAdminRole);
        return StatusCode(StatusCodes.Status403Forbidden, new ForbiddenErrorDto(ManualWriteRoleRefusal));
    }

    /// <summary>The 403 message of a manual write without the <c>StreamDataAdmin</c> role (AB#8590 AC 8).</summary>
    internal static readonly string ManualWriteRoleRefusal =
        $"Access is denied: a write with origin 'manual' requires the role '{CommonConstants.StreamDataAdminRole}' in the tenant. " +
        "The token carries no such role; ask a tenant administrator to grant it.";

    /// <summary>
    /// Parses the optional origin of a REST point: absent or empty is automated, otherwise <c>manual</c> or
    /// <c>automated</c>, case-insensitive. Numbers are refused on purpose: the wire value is the name.
    /// </summary>
    internal static bool TryParseOrigin(string? value, out ArchiveRowOrigin origin)
    {
        origin = ArchiveRowOrigin.Automated;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase))
        {
            origin = ArchiveRowOrigin.Manual;
            return true;
        }

        return string.Equals(value, "automated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The writer a manual row records (AB#7593, AB#7647), taken from the verified token, never from the body.
    /// For a user token the readable name: <c>preferred_username</c>, else <c>name</c>, else the subject id. A
    /// client-credentials token (no subject) records its client id. <c>null</c> when the token names neither.
    /// </summary>
    internal static string? CallerIdentity(System.Security.Claims.ClaimsPrincipal? user)
    {
        var subject = NonEmpty(user?.FindFirst("sub")?.Value)
                      ?? NonEmpty(user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        if (subject is null)
        {
            return NonEmpty(user?.FindFirst("client_id")?.Value);
        }

        return NonEmpty(user?.FindFirst("preferred_username")?.Value)
               ?? NonEmpty(user?.FindFirst("name")?.Value)
               ?? NonEmpty(user?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value)
               ?? subject;

        static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The archive snapshot the insert checks run against, or <c>null</c> when the archive cannot be read here — the
    /// repository then reports the real problem (unknown archive, not activated, stream data disabled) with its own message.
    /// </summary>
    private static async Task<ArchiveSnapshot?> LoadArchiveSnapshotAsync(ITenantContext tenantContext, string archiveRtId)
    {
        var store = tenantContext.GetArchiveRuntimeStore();
        return store is null ? null : await store.GetAsync(new OctoObjectId(archiveRtId));
    }

    /// <summary>
    /// The 400 message when an insert batch carries <c>ckTypeId</c> values that are not the archive's target, naming both
    /// sides; <c>null</c> when every value matches or the archive gives nothing to judge against. Shared by the raw and
    /// the time-range insert.
    /// </summary>
    private static string? CkTypeMismatchMessage(string archiveRtId, ArchiveSnapshot? snapshot, IEnumerable<string> ckTypeIds)
    {
        var (expected, values) = FindMismatchedCkTypeIds(snapshot, ckTypeIds);
        return expected is null || values.Count == 0
            ? null
            : $"Archive '{archiveRtId}' captures '{expected}'. The batch carries " +
              $"{string.Join(", ", values.Select(v => $"'{v}'"))}, whose rows would all be " +
              "discarded. Send the ckTypeId in its unversioned form, e.g. 'Model/Type'.";
    }

    /// <summary>
    /// The distinct <c>ckTypeId</c> values in an insert batch that are not the archive's target,
    /// together with the target itself. <c>Expected</c> is <c>null</c> when there is no snapshot.
    /// </summary>
    private static (string? Expected, IReadOnlyList<string> Values) FindMismatchedCkTypeIds(
        ArchiveSnapshot? snapshot, IEnumerable<string> ckTypeIds)
    {
        if (snapshot is null)
        {
            return (null, Array.Empty<string>());
        }

        var expected = snapshot.TargetCkTypeId;

        // An archive whose target type is not populated gives nothing to judge the batch against,
        // and RtCkId's own accessors throw on such a value rather than reporting it as empty. Skip
        // the guard instead of failing the insert on its diagnostic — this only decides whether a
        // batch is refused early, never whether the rows are written correctly.
        string expectedName;
        try
        {
            expectedName = expected.ToString();
        }
        catch (Exception e) when (e is NullReferenceException or ArgumentException)
        {
            return (null, Array.Empty<string>());
        }

        var values = ckTypeIds
            .Where(id => !MatchesTarget(id, expected))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (expectedName, values);
    }

    /// <summary>
    /// Whether a wire <c>ckTypeId</c> denotes <paramref name="expected"/>, decided by exactly the
    /// <see cref="RtCkId{T}"/> equality the repository filters on — a string comparison here could
    /// reject a value the repository would have accepted. A malformed id counts as a mismatch so it
    /// is reported alongside the others instead of surfacing as a bare parse error.
    /// </summary>
    private static bool MatchesTarget(string ckTypeId, RtCkId<CkTypeId> expected)
    {
        try
        {
            return new RtCkId<CkTypeId>(ckTypeId).Equals(expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns every non-soft-deleted rollup archive that declares the given archive as one of its
    /// sources (AB#5157: membership in <c>Sources</c>, any validity span). Concept §9.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/rollups")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<IReadOnlyList<RollupArchiveInfoRestDto>>> ListRollupsForArchive(
        [Required] string tenantId, [Required] string archiveRtId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var rollupStore = tenantContext.GetRollupArchiveRuntimeStore();
            if (rollupStore is null)
            {
                return Ok(Array.Empty<RollupArchiveInfoRestDto>());
            }

            var sourceRtId = new OctoObjectId(archiveRtId);
            var result = new List<RollupArchiveInfoRestDto>();
            await foreach (var rollup in rollupStore.EnumerateAsync())
            {
                if (!rollup.HasSource(sourceRtId)) continue;
                result.Add(new RollupArchiveInfoRestDto(
                    rollup.RtId.ToString(),
                    rollup.RtWellKnownName,
                    rollup.Status.ToString(),
                    rollup.SingleUnboundedSourceRtId?.ToString(),
                    (long)rollup.BucketSize.TotalMilliseconds,
                    (long)rollup.WatermarkLag.TotalMilliseconds,
                    rollup.LastAggregatedBucketEnd,
                    rollup.FrozenUntil,
                    rollup.Aggregations.Count,
                    rollup.RecomputeInProgress,
                    rollup.LastRecomputeStartedAt,
                    rollup.LastRecomputeSuccessAt,
                    rollup.LastRecomputeFailureAt,
                    rollup.LastRecomputeFailureReason,
                    rollup.DirtyWindowsPending,
                    rollup.PendingRecomputeRanges,
                    rollup.Sources
                        .Select(src => new RollupSourceRestDto(src.SourceArchiveRtId.ToString(), src.ValidFrom, src.ValidTo))
                        .ToList(),
                    rollup.ReferenceTimeZone,
                    rollup.DayStartOffsetMs));
            }
            return Ok(result);
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Returns the MEASURED data coverage of an archive family (AB#5157): the given archive first, then
    /// every rollup that transitively depends on it (breadth-first, once each), each with its grain and
    /// the earliest / latest timestamp that holds data. Returns an empty list when stream data is not
    /// enabled for the tenant or the archive is unknown. Same projection as the <c>coverageFor</c>
    /// GraphQL query.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/coverage")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<IReadOnlyList<ArchiveCoverageRestDto>>> GetArchiveCoverage(
        [Required] string tenantId, [Required] string archiveRtId, CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var coverageService = tenantContext.GetArchiveFamilyCoverageService();
            if (coverageService is null)
            {
                return Ok(Array.Empty<ArchiveCoverageRestDto>());
            }

            // Bound by the framework to HttpContext.RequestAborted, which keeps the probe tied to the
            // caller without dereferencing HttpContext — it is null whenever the action runs outside
            // a request pipeline.
            var rungs = await coverageService.GetFamilyCoverageAsync(
                new OctoObjectId(archiveRtId), cancellationToken);
            return Ok(rungs.Select(ArchiveCoverageRestDto.From).ToList());
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    private async Task<IActionResult> InvokeArchiveTransitionAsync(
        string tenantId, string archiveRtId, string operation,
        Func<IArchiveLifecycleService, OctoObjectId, Task> transition)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var lifecycle = tenantContext.GetArchiveLifecycleService()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");
            await transition(lifecycle, new OctoObjectId(archiveRtId));
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("{Operation} refused for tenant '{TenantId}', archive '{ArchiveRtId}': {Reason}",
                operation, tenantId, archiveRtId, e.Message);
            return BadRequest(e.Message);
        }
    }

    private async Task<IActionResult> InvokeRollupAsync(
        string tenantId, string rollupRtId, string operation,
        Func<IRollupArchiveLifecycleService, OctoObjectId, Task> mutation)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var lifecycle = tenantContext.GetRollupArchiveLifecycleService()
                ?? throw new StreamDataException(
                    $"Rollup support is not wired for tenant '{tenantId}'. Ensure stream data is enabled and a rollup store is registered.");
            await mutation(lifecycle, new OctoObjectId(rollupRtId));
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("{Operation} refused for tenant '{TenantId}', rollup '{RollupRtId}': {Reason}",
                operation, tenantId, rollupRtId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Interprets an offset-free repair bound as UTC (like the GraphQL mutation) instead of the server's local time zone.
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc)
    };
}
