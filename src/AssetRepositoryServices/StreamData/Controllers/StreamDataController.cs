using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using GraphQL;
using Duende.IdentityModel;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure;
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
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
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
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
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
    /// Adds a computed column to an Activated raw or time-range archive and backfills it across the
    /// existing rows (AB#4189 Phase 7). The column stays hidden until the backfill completes, then
    /// becomes visible atomically; a backfill failure leaves the previous archive state intact. Same
    /// lifecycle path as the <c>addComputedColumn</c> GraphQL mutation.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/computed-columns")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> AddComputedColumn(
        [Required] string tenantId, [Required] string archiveRtId,
        [Required] string name, [Required] string formula, [Required] FormulaResultType resultType,
        bool indexed = true)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "AddComputedColumn",
            (lifecycle, id) => lifecycle.AddComputedColumnAsync(id, name, formula, resultType, indexed));

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
    /// backfilled, then switch atomically. Rejected when another computed column references this one.
    /// </summary>
    [HttpPut("archives/{archiveRtId}/computed-columns/{name}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    public Task<IActionResult> UpdateComputedColumnFormula(
        [Required] string tenantId, [Required] string archiveRtId, [Required] string name,
        [Required] string formula)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "UpdateComputedColumnFormula",
            (lifecycle, id) => lifecycle.UpdateComputedColumnFormulaAsync(id, name, formula));

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
    /// Inserts a batch of externally-aggregated time-range data points into a
    /// <c>TimeRangeArchive</c>. Each row covers a half-open <c>[from, to)</c> window;
    /// re-deliveries upsert via the natural key and set the row's <c>was_updated</c> flag to
    /// true. Time-range concept §3. The archive must be in <c>Activated</c> status; non-time-
    /// range archives reject the call with HTTP 400.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/insertTimeRange")]
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
            var mismatched = await FindMismatchedCkTypeIdsAsync(tenantContext, archiveRtId, points);
            if (mismatched is { Expected: { } expected, Values.Count: > 0 })
            {
                return BadRequest(
                    $"Archive '{archiveRtId}' captures '{expected}'. The batch carries " +
                    $"{string.Join(", ", mismatched.Values.Select(v => $"'{v}'"))}, whose rows would all be " +
                    "discarded. Send the ckTypeId in its unversioned form, e.g. 'Model/Type'.");
            }

            var domainPoints = points.Select(p => new TimeRangeStreamDataPoint
            {
                RtId = new OctoObjectId(p.RtId),
                CkTypeId = new RtCkId<CkTypeId>(p.CkTypeId),
                From = p.From,
                To = p.To,
                RtWellKnownName = p.RtWellKnownName,
                Attributes = p.Attributes,
            }).ToList();

            await repository.InsertTimeRangeAsync(new OctoObjectId(archiveRtId), domainPoints);
            return NoContent();
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
    /// The distinct <c>ckTypeId</c> values in an insert batch that are not the archive's target,
    /// together with the target itself. <c>Expected</c> is <c>null</c> when the archive cannot be
    /// read here — the repository then reports the real problem (unknown archive, not activated,
    /// stream data disabled) with its own message.
    /// </summary>
    private static async Task<(string? Expected, IReadOnlyList<string> Values)> FindMismatchedCkTypeIdsAsync(
        ITenantContext tenantContext, string archiveRtId, IReadOnlyList<InsertTimeRangePointRestDto> points)
    {
        var store = tenantContext.GetArchiveRuntimeStore();
        if (store is null)
        {
            return (null, Array.Empty<string>());
        }

        var snapshot = await store.GetAsync(new OctoObjectId(archiveRtId));
        if (snapshot is null)
        {
            return (null, Array.Empty<string>());
        }

        var expected = snapshot.TargetCkTypeId;
        var values = points
            .Select(p => p.CkTypeId)
            .Where(id => !MatchesTarget(id, expected))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (expected.ToString(), values);
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
                        .ToList()));
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
        [Required] string tenantId, [Required] string archiveRtId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var coverageService = tenantContext.GetArchiveFamilyCoverageService();
            if (coverageService is null)
            {
                return Ok(Array.Empty<ArchiveCoverageRestDto>());
            }

            var rungs = await coverageService.GetFamilyCoverageAsync(
                new OctoObjectId(archiveRtId), HttpContext.RequestAborted);
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
}
