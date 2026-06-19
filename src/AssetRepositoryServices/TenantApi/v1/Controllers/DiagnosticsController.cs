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
    /// <param name="limit">Maximum number of entries to return (default 100, max 1000).</param>
    [HttpGet("slow-mongo-queries")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<SlowQueryEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    public IActionResult GetSlowMongoQueries(
        [FromQuery] string? commandName = null,
        [FromQuery] int? minDurationMs = null,
        [FromQuery] int? sinceMinutes = null,
        [FromQuery] int limit = 100)
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

        var entries = _buffer.GetSnapshot(
            predicate: e =>
                // Tenant scoping is enforced server-side from the validated route tenantId —
                // never from a query parameter. The buffer's Database field is the trusted
                // attribution dimension written by MongoCommandObservability.
                string.Equals(e.Database, tenantId, StringComparison.Ordinal)
                && (commandName is null || string.Equals(e.CommandName, commandName, StringComparison.Ordinal))
                && (!minDurationMs.HasValue || e.DurationMs >= minDurationMs.Value)
                && (!sinceCutoff.HasValue || e.Timestamp >= sinceCutoff.Value),
            limit: clampedLimit);

        var dtos = entries.Select(e => new SlowQueryEntryDto(
            Timestamp: e.Timestamp,
            CommandName: e.CommandName,
            Target: e.Target,
            DurationMs: e.DurationMs,
            RequestId: e.RequestId,
            CommandBsonPreview: e.CommandBsonPreview,
            Success: e.Success,
            ErrorCode: e.ErrorCode)).ToList();

        return Ok(dtos);
    }
}
