using Asp.Versioning;

using IdentityModel;

using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
/// Tenant-scoped diagnostics endpoints. Currently surfaces the in-memory slow-query buffer for
/// the Refinery Studio Slow Queries page (AB#4212).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class DiagnosticsController : ControllerBase
{
    private readonly SlowQueriesBuffer _buffer;
    private readonly IIndexUsageService _indexUsageService;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="buffer">DI-resolved in-memory ring of recent slow MongoDB commands.</param>
    /// <param name="indexUsageService">Stage 3 / AB#4224 unused-index analysis service.</param>
    public DiagnosticsController(SlowQueriesBuffer buffer, IIndexUsageService indexUsageService)
    {
        _buffer = buffer;
        _indexUsageService = indexUsageService;
    }

    /// <summary>
    /// Returns the most-recent slow MongoDB commands observed for the current tenant.
    /// </summary>
    /// <remarks>
    /// The buffer is in-memory and lives for the lifetime of the asset-repo-services process —
    /// service restarts reset it. The Studio surface communicates this transparently
    /// ("since service start"). Each entry includes a truncated BSON preview so a user can
    /// identify the exact filter / pipeline that ran slowly.
    /// </remarks>
    /// <param name="commandName">Optional exact match on the driver-level command name (e.g. <c>find</c>, <c>aggregate</c>).</param>
    /// <param name="minDurationMs">Optional minimum duration in milliseconds; entries below this are excluded.</param>
    /// <param name="sinceMinutes">Optional time window; only entries newer than <c>now - sinceMinutes</c> are returned.</param>
    /// <param name="limit">Maximum number of entries (or groups) to return (default 100, max 1000).</param>
    /// <param name="groupBy">Set to <c>fingerprint</c> to return aggregated <c>SlowQueryGroupDto</c> rows (one per structural fingerprint) instead of per-call entries. Any other value is treated as no grouping.</param>
    [HttpGet("slow-mongo-queries")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<SlowQueryEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<SlowQueryGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public IActionResult GetSlowMongoQueries(
        [FromQuery] string? commandName = null,
        [FromQuery] int? minDurationMs = null,
        [FromQuery] int? sinceMinutes = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? groupBy = null)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new OperationFailedErrorDto("TenantId is required"));
        }

        // Clamp limit so a misconfigured client can't pull the whole buffer in one request.
        var clampedLimit = Math.Clamp(limit, 1, 1000);

        DateTimeOffset? sinceCutoff = sinceMinutes.HasValue
            ? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(sinceMinutes.Value)
            : null;

        // Tenant scoping is enforced server-side from the validated route tenantId — never
        // from a query parameter. The buffer's Database field is the trusted attribution
        // dimension written by MongoCommandObservability.
        bool MatchesFilter(SlowQueryEntry e)
            => string.Equals(e.Database, tenantId, StringComparison.Ordinal)
               && (commandName is null || string.Equals(e.CommandName, commandName, StringComparison.Ordinal))
               && (!minDurationMs.HasValue || e.DurationMs >= minDurationMs.Value)
               && (!sinceCutoff.HasValue || e.Timestamp >= sinceCutoff.Value);

        if (string.Equals(groupBy, "fingerprint", StringComparison.OrdinalIgnoreCase))
        {
            var groups = _buffer.GetGroupedSnapshot(predicate: MatchesFilter, limit: clampedLimit);
            var groupDtos = groups.Select(g => new SlowQueryGroupDto(
                Fingerprint: g.Fingerprint,
                CommandName: g.CommandName,
                Target: g.Target,
                Count: g.Count,
                FirstSeen: g.FirstSeen,
                LastSeen: g.LastSeen,
                MinDurationMs: g.MinDurationMs,
                MaxDurationMs: g.MaxDurationMs,
                AvgDurationMs: g.AvgDurationMs,
                Representative: ToDto(g.Representative),
                Explain: ToExplainDto(g.Explain))).ToList();

            return Ok(groupDtos);
        }

        var entries = _buffer.GetSnapshot(predicate: MatchesFilter, limit: clampedLimit);
        var dtos = entries.Select(ToDto).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Stage 3 / AB#4224 unused-index analysis. Runs MongoDB's <c>$indexStats</c> aggregation
    /// across every non-system collection in the tenant's database, sums ops across replica-set
    /// hosts, takes the earliest <c>accesses.since</c>, and classifies each index as
    /// <c>builtin</c> / <c>unused</c> / <c>lowUsage</c> / <c>used</c>. The asset-repo controller
    /// always sees the snapshot at the moment the call hits MongoDB — there is no background
    /// poller or persisted history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>accesses.since</c> resets on <c>mongod</c> restart. <paramref name="minAgeDays"/>
    /// shields the operator from false-positive Unused flags immediately after a restart by
    /// pinning anything younger as <c>used</c> regardless of the ops count.
    /// </para>
    /// <para>
    /// Default ordering: <c>unused</c> first, then <c>lowUsage</c>, then everything else.
    /// <paramref name="includeUsed"/> defaults to false so the operator sees only the
    /// actionable rows by default; setting <c>true</c> appends <c>builtin</c> and <c>used</c>
    /// for visibility.
    /// </para>
    /// </remarks>
    /// <param name="minAgeDays">Lower bound on observation window (days) before an index can be
    /// flagged as Unused / LowUsage. Younger indexes return as <c>used</c> regardless of ops.
    /// Default <c>7</c>.</param>
    /// <param name="lowUsageOps">Strict less-than cutoff: ops below this count classify as
    /// <c>lowUsage</c>, at-or-above as <c>used</c>. Default <c>10</c>.</param>
    /// <param name="includeUsed">When <c>true</c>, return all rows including <c>builtin</c>
    /// and <c>used</c>. Default <c>false</c> returns only <c>unused</c> + <c>lowUsage</c>.</param>
    /// <param name="cancellationToken">Cancellation token from the request pipeline.</param>
    [HttpGet("index-usage")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<IndexUsageEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIndexUsageAsync(
        [FromQuery] int minAgeDays = 7,
        [FromQuery] long lowUsageOps = 10,
        [FromQuery] bool includeUsed = false,
        CancellationToken cancellationToken = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new OperationFailedErrorDto("TenantId is required"));
        }

        // Defensive clamps — keep operator-supplied values inside a sensible range so a typo
        // ("minAgeDays=-1" or a 1B ops threshold) can't silently invert the classification.
        var clampedMinAge = Math.Max(0, minAgeDays);
        var clampedLowUsage = Math.Max(0L, lowUsageOps);

        var entries = await _indexUsageService.CollectAsync(
            tenantId, clampedMinAge, clampedLowUsage, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        // Map → DTO and apply visibility filter + actionable ordering server-side. The Studio
        // surface only renders what comes back, so the rule for "show me the noise-free
        // default" lives here, not in the engine collector.
        var dtos = entries
            .Where(e => includeUsed
                || e.Status == IndexUsageStatus.Unused
                || e.Status == IndexUsageStatus.LowUsage)
            .Select(ToIndexUsageDto)
            .OrderBy(StatusOrder)
            .ThenBy(e => e.CollectionName, StringComparer.Ordinal)
            .ThenBy(e => e.IndexName, StringComparer.Ordinal)
            .ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Sort key for the index-usage surface — Unused first (most actionable), LowUsage second,
    /// then Builtin/Used for visibility when <c>includeUsed=true</c>. The classifier already
    /// guarantees these are the only possible values; the catch-all keeps the compiler happy
    /// without silently dropping a future engine-side enum addition off the end of the list.
    /// </summary>
    private static int StatusOrder(IndexUsageEntryDto e) => e.Status switch
    {
        "unused" => 0,
        "lowUsage" => 1,
        "builtin" => 2,
        "used" => 3,
        _ => 4
    };

    /// <summary>
    /// Projects the engine's <see cref="IndexUsageEntry"/> record onto the API-stable
    /// <see cref="IndexUsageEntryDto"/>. Status is flattened to a lowercase string —
    /// same passthrough pattern Stage 2D's <see cref="ToSuggestionDto"/> uses, so a future
    /// engine-side enum addition surfaces transparently on the wire instead of being
    /// silently coerced.
    /// </summary>
    private static IndexUsageEntryDto ToIndexUsageDto(IndexUsageEntry e) => new(
        CollectionName: e.CollectionName,
        IndexName: e.IndexName,
        KeySpec: e.KeySpec,
        OpsCount: e.OpsCount,
        SinceUtc: e.SinceUtc,
        AgeDays: e.AgeDays,
        IsBuiltin: e.IsBuiltin,
        DropShellCommand: e.DropShellCommand,
        // Camel-case for multi-word values keeps the wire shape aligned with JS-style
        // identifiers — same precedent as SlowQueryIndexSuggestionDto.Confidence ("low" /
        // "medium" / "high").
        Status: e.Status switch
        {
            IndexUsageStatus.Builtin => "builtin",
            IndexUsageStatus.Unused => "unused",
            IndexUsageStatus.LowUsage => "lowUsage",
            IndexUsageStatus.Used => "used",
            _ => e.Status.ToString().ToLowerInvariant()
        });

    private static SlowQueryEntryDto ToDto(SlowQueryEntry e) => new(
        Timestamp: e.Timestamp,
        CommandName: e.CommandName,
        Target: e.Target,
        DurationMs: e.DurationMs,
        RequestId: e.RequestId,
        CommandBsonPreview: e.CommandBsonPreview,
        Success: e.Success,
        ErrorCode: e.ErrorCode,
        Fingerprint: e.Fingerprint,
        Explain: ToExplainDto(e.Explain));

    /// <summary>
    /// Projects the engine's <see cref="SlowQueryExplain"/> record onto the API-stable
    /// <see cref="SlowQueryExplainDto"/>. The status enum is flattened to a lower-case string
    /// (<c>"success"</c> / <c>"unsupported"</c> / <c>"failed"</c>) so a future engine-side
    /// enum rename doesn't break the contract. Returns <c>null</c> when no explain has landed
    /// yet — same shape callers already handle for unparsed slow queries.
    /// </summary>
    private static SlowQueryExplainDto? ToExplainDto(SlowQueryExplain? explain)
    {
        if (explain is null)
        {
            return null;
        }

        var status = explain.Status switch
        {
            SlowQueryExplainStatus.Success => "success",
            SlowQueryExplainStatus.Unsupported => "unsupported",
            SlowQueryExplainStatus.Failed => "failed",
            _ => "failed"
        };

        return new SlowQueryExplainDto(
            CapturedAt: explain.CapturedAt,
            Status: status,
            WinningStage: explain.WinningStage,
            HasCollScan: explain.HasCollScan,
            IndexNames: explain.IndexNames,
            RawExplainPreview: explain.RawExplainPreview,
            ErrorMessage: explain.ErrorMessage,
            IndexSuggestion: ToSuggestionDto(explain.IndexSuggestion));
    }

    /// <summary>
    /// Projects the engine's <see cref="SlowQueryIndexSuggestion"/> onto the API-stable
    /// <see cref="SlowQueryIndexSuggestionDto"/>. Confidence and field-kind enums are both
    /// flattened to lowercase strings so a future engine-side enum rename doesn't break the
    /// public contract. Returns <c>null</c> when no suggestion was attached.
    /// </summary>
    private static SlowQueryIndexSuggestionDto? ToSuggestionDto(SlowQueryIndexSuggestion? s)
    {
        if (s is null)
        {
            return null;
        }

        // Passthrough lower-casing of the enum name keeps the wire stable for the values we
        // know AND surfaces any future engine-side addition transparently (e.g. an upcoming
        // "Estimated" confidence would arrive on the wire as "estimated" rather than getting
        // silently coerced to "low" and misleading the operator).
        var confidence = s.Confidence.ToString().ToLowerInvariant();

        var fields = s.Fields.Select(f => new SlowQueryIndexFieldDto(
            Name: f.Name,
            Direction: f.Direction,
            // Same passthrough rationale — a future engine-side field kind (e.g. "Hashed")
            // surfaces accurately to the client rather than being remapped to "equality".
            Kind: f.Kind.ToString().ToLowerInvariant())).ToList();

        return new SlowQueryIndexSuggestionDto(
            IndexName: s.IndexName,
            Fields: fields,
            ShellCommand: s.ShellCommand,
            Confidence: confidence,
            Notes: s.Notes,
            // Stage 2D — straight passthrough. The engine emits a paste-ready snippet; we
            // surface it verbatim so the Studio's clipboard copy is byte-identical to what
            // the operator pastes into their CK source.
            CkYamlSnippet: s.CkYamlSnippet,
            CkTypeFullName: s.CkTypeFullName);
    }
}
