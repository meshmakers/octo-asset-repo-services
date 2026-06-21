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

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="buffer">DI-resolved in-memory ring of recent slow MongoDB commands.</param>
    public DiagnosticsController(SlowQueriesBuffer buffer)
    {
        _buffer = buffer;
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
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
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
                Representative: ToDto(g.Representative))).ToList();

            return Ok(groupDtos);
        }

        var entries = _buffer.GetSnapshot(predicate: MatchesFilter, limit: clampedLimit);
        var dtos = entries.Select(ToDto).ToList();

        return Ok(dtos);
    }

    private static SlowQueryEntryDto ToDto(SlowQueryEntry e) => new(
        Timestamp: e.Timestamp,
        CommandName: e.CommandName,
        Target: e.Target,
        DurationMs: e.DurationMs,
        RequestId: e.RequestId,
        CommandBsonPreview: e.CommandBsonPreview,
        Success: e.Success,
        ErrorCode: e.ErrorCode,
        Fingerprint: e.Fingerprint);
}
