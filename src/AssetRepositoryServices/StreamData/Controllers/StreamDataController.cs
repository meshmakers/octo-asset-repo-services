using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using GraphQL;
using IdentityModel;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST Controller for stream data management
/// </summary>

[Microsoft.AspNetCore.Authorization.Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class StreamDataController : ControllerBase
{
    private readonly ILogger<StreamDataController> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IOptions<StreamDataInstanceConfiguration> _instanceConfiguration;

    /// <summary>
    /// Constructor
    /// </summary>
    public StreamDataController(
        ILogger<StreamDataController> logger,
        ISystemContext systemContext,
        IOptions<StreamDataInstanceConfiguration> instanceConfiguration)
    {
        _logger = logger;
        _systemContext = systemContext;
        _instanceConfiguration = instanceConfiguration;
    }

    /// <summary>
    /// Returns the StreamData feature availability flags for the current tenant. Exposed to the
    /// Refinery Studio so it can decide whether to render the archives navigation at all
    /// (instance-level) and whether tenants need to opt in (tenant-level). Concept §6.
    /// </summary>
    /// <param name="tenantId">Tenant whose flag is reported.</param>
    [HttpGet("status")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<ActionResult<StreamDataStatusDto>> Status([Required] string tenantId)
    {
        var instanceEnabled = _instanceConfiguration.Value.Enabled;

        var tenantEnabled = false;
        if (instanceEnabled)
        {
            try
            {
                var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
                tenantEnabled = await tenantContext.IsStreamDataEnabledAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StreamData status: failed to read tenant flag for '{TenantId}'; defaulting to false.",
                    tenantId);
            }
        }

        return Ok(new StreamDataStatusDto
        {
            InstanceEnabled = instanceEnabled,
            TenantEnabled = tenantEnabled,
        });
    }

    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    [HttpPost("enable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> ActivateArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "ActivateArchive",
            (lifecycle, id) => lifecycle.ActivateAsync(id));

    /// <summary>
    /// Disables stream data for a given tenant
    /// </summary>
    [HttpPost("disable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public async Task<IActionResult> Disable([Required] string tenantId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.DisableStreamDataAsync();
            return NoContent();
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
    /// Disables a CkArchive: transitions to <c>Disabled</c> (no DDL side-effect; data preserved).
    /// Allowed only from <c>Activated</c>.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/disable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> DisableArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "DisableArchive",
            (lifecycle, id) => lifecycle.DisableAsync(id));

    /// <summary>
    /// Re-enables a previously disabled archive: transitions <c>Disabled → Activated</c>. Re-validates
    /// column paths against the current CK model; no DDL because the table already exists.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/enable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> EnableArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "EnableArchive",
            (lifecycle, id) => lifecycle.EnableAsync(id));

    /// <summary>
    /// Retries activation after a previous DDL failure. Allowed only from <c>Failed</c>.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/retry")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> RetryArchiveActivation([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "RetryArchiveActivation",
            (lifecycle, id) => lifecycle.RetryActivationAsync(id));

    /// <summary>
    /// Drops the per-archive CrateDB table (idempotent) and soft-deletes the <c>CkArchive</c>
    /// entity. Destructive — historical data is lost. Allowed from any status.
    /// </summary>
    [HttpDelete("archives/{archiveRtId}")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> DeleteArchive([Required] string tenantId, [Required] string archiveRtId)
        => InvokeArchiveTransitionAsync(tenantId, archiveRtId, "DeleteArchive",
            (lifecycle, id) => lifecycle.DeleteAsync(id));

    /// <summary>
    /// Freezes a rollup archive at <paramref name="until"/>. Monotonic — rejected when the new
    /// value is earlier than the current FrozenUntil. Rollup-archives concept §9.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/freeze")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> FreezeRollupArchive(
        [Required] string tenantId, [Required] string rollupRtId, [Required] DateTime until)
        => InvokeRollupAsync(tenantId, rollupRtId, "FreezeRollup",
            (lifecycle, id) => lifecycle.FreezeAsync(id, until));

    /// <summary>
    /// Clears FrozenUntil on the rollup archive. Idempotent. Concept §9.
    /// </summary>
    [HttpPost("archives/{rollupRtId}/unfreeze")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public Task<IActionResult> RewindRollupWatermark(
        [Required] string tenantId, [Required] string rollupRtId, [Required] DateTime toBucketEnd)
        => InvokeRollupAsync(tenantId, rollupRtId, "RewindRollup",
            (lifecycle, id) => lifecycle.RewindWatermarkAsync(id, toBucketEnd));

    /// <summary>
    /// Inserts a batch of externally-aggregated time-range data points into a
    /// <c>TimeRangeArchive</c>. Each row covers a half-open <c>[from, to)</c> window;
    /// re-deliveries upsert via the natural key and set the row's <c>was_updated</c> flag to
    /// true. Time-range concept §3. The archive must be in <c>Activated</c> status; non-time-
    /// range archives reject the call with HTTP 400.
    /// </summary>
    [HttpPost("archives/{archiveRtId}/insertTimeRange")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
    /// Returns every non-soft-deleted rollup archive attached to the given source archive.
    /// Concept §9.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/rollups")]
    [Microsoft.AspNetCore.Authorization.Authorize]
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
                if (rollup.SourceArchiveRtId != sourceRtId) continue;
                result.Add(new RollupArchiveInfoRestDto(
                    rollup.RtId.ToString(),
                    rollup.RtWellKnownName,
                    rollup.Status.ToString(),
                    rollup.SourceArchiveRtId.ToString(),
                    (long)rollup.BucketSize.TotalMilliseconds,
                    (long)rollup.WatermarkLag.TotalMilliseconds,
                    rollup.LastAggregatedBucketEnd,
                    rollup.FrozenUntil,
                    rollup.Aggregations.Count));
            }
            return Ok(result);
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Returns the archive's schema block (rtId, well-known name, kind, target CK type, configured
    /// columns, and — when applicable — rollup aggregations / time-range period) for the
    /// export/import pre-flight (AB#4230 §4.2 / §6). The bot export job embeds this verbatim into the
    /// ZIP's <c>metadata.archive</c>; the import job compares it against the live target before any
    /// write. Read-only.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/schema")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<ArchiveSchemaDto>> GetArchiveSchema(
        [Required] string tenantId, [Required] string archiveRtId)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var store = tenantContext.GetArchiveRuntimeStore();

            var snapshot = await store.GetAsync(new OctoObjectId(archiveRtId));
            if (snapshot is null)
            {
                return NotFound($"Archive '{archiveRtId}' was not found in tenant '{tenantId}'.");
            }

            return Ok(MapSchema(snapshot));
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("GetArchiveSchema refused for tenant '{TenantId}', archive '{ArchiveRtId}': {Reason}",
                tenantId, archiveRtId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Streams the archive's rows as <c>application/x-ndjson</c> (one JSON object per line, chunked,
    /// no server-side buffering) for the bot export job (AB#4230 §4.2). <paramref name="fromUtc"/> /
    /// <paramref name="toUtc"/> are optional: both omitted ⇒ whole archive; one supplied ⇒ the other
    /// bound is treated as open. The window is the half-open <c>[from, to)</c> slice (decision #5).
    /// Read-only.
    /// </summary>
    [HttpGet("archives/{archiveRtId}/export-stream")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    public async Task ExportStream(
        [Required] string tenantId, [Required] string archiveRtId,
        DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        IStreamDataRepository repository;
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            // Pre-validate the archive exists BEFORE we set the 200 status — once we start writing
            // the NDJSON body the status line is committed and a mid-stream failure can't be turned
            // into a clean 4xx. ExportRowsAsync resolves the snapshot lazily on first enumeration, so
            // we resolve it eagerly here to surface a missing archive as a 400.
            var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(new OctoObjectId(archiveRtId));
            if (snapshot is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                await Response.WriteAsync($"Archive '{archiveRtId}' was not found in tenant '{tenantId}'.", cancellationToken);
                return;
            }
        }
        catch (Exception e) when (e is ConfigurationException or StreamDataException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync(e.Message, cancellationToken);
            return;
        }

        var window = BuildWindow(fromUtc, toUtc);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), bufferSize: 64 * 1024);
        var rowsSinceFlush = 0;
        await foreach (var row in repository.ExportRowsAsync(new OctoObjectId(archiveRtId), window, cancellationToken))
        {
            var line = JsonSerializer.Serialize(row, NdjsonOptions);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);

            // Flush every ~256 rows so a slow client / proxy sees progress and the chunked transfer
            // streams rather than coalescing into one big buffer.
            if (++rowsSinceFlush >= 256)
            {
                await writer.FlushAsync(cancellationToken);
                rowsSinceFlush = 0;
            }
        }

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Consumes <c>application/x-ndjson</c> from the request body (one archive row per line) and
    /// bulk-imports it into the target archive via <c>ImportRowsAsync</c> (AB#4230 §4.2). The body is
    /// read line-by-line into an async stream so the dataset is never fully buffered. Returns 204.
    /// <paramref name="mode"/> selects InsertOnly vs Upsert (default Upsert for the windowed path).
    /// </summary>
    [HttpPost("archives/{archiveRtId}/import-stream")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public async Task<IActionResult> ImportStream(
        [Required] string tenantId, [Required] string archiveRtId,
        ArchiveImportMode mode = ArchiveImportMode.Upsert, CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetStreamDataRepository()
                ?? throw new StreamDataException(
                    $"StreamData is not enabled for tenant '{tenantId}'. Call POST /streamdata/enable first.");

            await repository.ImportRowsAsync(
                new OctoObjectId(archiveRtId),
                ReadNdjsonRowsAsync(Request.Body, cancellationToken),
                mode,
                cancellationToken);

            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
        catch (StreamDataException e)
        {
            _logger.LogWarning("ImportStream refused for tenant '{TenantId}', archive '{ArchiveRtId}': {Reason}",
                tenantId, archiveRtId, e.Message);
            return BadRequest(e.Message);
        }
        catch (ArgumentException e)
        {
            // Covers the per-field rtid-hex / required-column validation in ImportRowsAsync.
            return BadRequest(e.Message);
        }
    }

    private static readonly JsonSerializerOptions NdjsonOptions = new(JsonSerializerDefaults.Web);

    private static TimeWindow? BuildWindow(DateTime? fromUtc, DateTime? toUtc)
    {
        if (fromUtc is null && toUtc is null)
        {
            return null; // whole archive
        }

        // One bound supplied ⇒ treat the other as open (concept §4.2).
        var from = (fromUtc ?? DateTime.MinValue).ToUniversalTime();
        var to = (toUtc ?? DateTime.MaxValue).ToUniversalTime();
        return new TimeWindow(from, to);
    }

    /// <summary>
    /// Reads the NDJSON request body one line at a time, deserialising each non-blank line into a
    /// row dictionary. Streamed (never fully buffered) so multi-GB imports stay flat in memory.
    /// </summary>
    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadNdjsonRowsAsync(
        Stream body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: true);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(line, NdjsonOptions);
            if (row is not null)
            {
                yield return row;
            }
        }
    }

    private static ArchiveSchemaDto MapSchema(ArchiveSnapshot snapshot)
    {
        var kind = snapshot.RollupAggregations is not null ? "rollup"
            : snapshot.IsTimeRange ? "timeRange"
            : "raw";

        var columns = snapshot.Columns
            .Select(c => new ArchiveSchemaColumnDto(c.Path, c.Indexed, c.Required))
            .ToList();

        var rollupAggregations = snapshot.RollupAggregations?
            .Select(a => new ArchiveSchemaRollupAggregationDto(a.SourcePath, a.Function.ToString(), a.TargetColumnName))
            .ToList();

        return new ArchiveSchemaDto(
            snapshot.RtId.ToString(),
            snapshot.RtWellKnownName,
            kind,
            snapshot.TargetCkTypeId.ToString(),
            columns,
            rollupAggregations,
            snapshot.Period is { } p ? (long)p.TotalMilliseconds : null);
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
