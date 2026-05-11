using System.ComponentModel.DataAnnotations;
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
            var rollupStore = tenantContext.GetCkRollupArchiveRuntimeStore();
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
