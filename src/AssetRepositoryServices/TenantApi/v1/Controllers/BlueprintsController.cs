using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-specific blueprint management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class BlueprintsController : ControllerBase
{
    private readonly ITenantBlueprintHistory _blueprintHistory;
    private readonly IBlueprintService _blueprintService;
    private readonly ITenantBlueprintInstallations _blueprintInstallations;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<BlueprintsController> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="blueprintHistory">Blueprint history service</param>
    /// <param name="blueprintService">Blueprint service</param>
    /// <param name="blueprintInstallations">Tenant blueprint installations service</param>
    /// <param name="authorizationService">Authorization service (checks the policy a forced breaking update needs)</param>
    /// <param name="logger">Logger</param>
    public BlueprintsController(
        ITenantBlueprintHistory blueprintHistory,
        IBlueprintService blueprintService,
        ITenantBlueprintInstallations blueprintInstallations,
        IAuthorizationService authorizationService,
        ILogger<BlueprintsController> logger)
    {
        _blueprintHistory = blueprintHistory;
        _blueprintService = blueprintService;
        _blueprintInstallations = blueprintInstallations;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    // POST {tenantId}/v1/blueprints/apply
    /// <summary>
    ///     Applies a blueprint to the tenant for the first time. CK models are
    ///     loaded into the tenant and seed data is imported via upsert.
    ///     <para>
    ///         Tenant upgrade guard (CK v2 F2.5, AB#6871): when the CK models the blueprint installs or raises would
    ///         break an installed model the request is refused with <c>409 CK_UPGRADE_BLOCKED</c> (problem details with
    ///         the structured blockers) before the first change. <c>ForceBreakingUpdate = true</c> (data model
    ///         management policy) applies anyway and logs the blockers and the caller at WARN.
    ///     </para>
    /// </summary>
    /// <param name="request">Apply request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Apply result with summary of changes</returns>
    [HttpPost("apply")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(BlueprintApplyResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Apply(
        [FromBody] BlueprintApplyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrEmpty(request.BlueprintId))
            {
                return BadRequest(new OperationFailedErrorDto("BlueprintId is required"));
            }

            BlueprintId blueprintId;
            try
            {
                blueprintId = new BlueprintId(request.BlueprintId);
            }
            catch (ArgumentException e)
            {
                return BadRequest(new OperationFailedErrorDto($"Invalid BlueprintId: {e.Message}"));
            }

            if (request.ForceBreakingUpdate && !await MayForceBreakingUpdateAsync())
            {
                return Forbid();
            }

            var result = await _blueprintService.ApplyBlueprintAsync(
                tenantId,
                blueprintId,
                new BlueprintApplyOptions
                {
                    Force = request.Force,
                    CkUpgradeGuard = GuardMode(request.ForceBreakingUpdate)
                },
                cancellationToken);

            if (!result.IsSuccess)
            {
                if (result.IsCkUpgradeBlocked)
                {
                    return CkUpgradeBlockedError.CreateBlueprintResult(blueprintId.FullName, result.CkUpdateSet,
                        BlueprintUpdatePreviewMapper.ToDtos(result.CkUpgradeBlockers));
                }

                if (result.CkUpgradeGuardFailed)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(
                        string.Join(", ", result.OperationResult.Messages
                            .Where(m => m.MessageLevel == MessageLevel.Error)
                            .Select(m => m.MessageText))));
                }

                var errors = result.OperationResult.Messages
                    .Where(m => m.MessageLevel == MessageLevel.Error)
                    .Select(m => m.MessageText)
                    .ToList();

                return BadRequest(new OperationFailedErrorDto(
                    string.Join(", ", errors.Count > 0 ? errors : ["Blueprint apply failed"])));
            }

            LogForcedBreakingUpdate(tenantId, blueprintId.FullName, result.CkUpdateSet, result.CkUpgradeBlockers);

            var response = new BlueprintApplyResultDto
            {
                Success = true,
                CkUpdateSet = result.CkUpdateSet.ToList(),
                Blockers = BlueprintUpdatePreviewMapper.ToDtos(result.CkUpgradeBlockers),
                TenantId = result.TenantId ?? tenantId,
                BlueprintId = result.BlueprintId?.FullName ?? request.BlueprintId,
                ApplicationMode = (request.Force
                    ? BlueprintApplicationMode.ReApply
                    : BlueprintApplicationMode.Initial).ToString(),
                SeedDataFilesApplied = result.AppliedSeedDataFiles.Count,
                LoadedCkModels = result.LoadedCkModels.Select(m => m.ToString()).ToList(),
                Warnings = result.OperationResult.Messages
                    .Where(m => m.MessageLevel == MessageLevel.Warning)
                    .Select(m => m.MessageText)
                    .ToList()
            };

            return Ok(response);
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

    // GET {tenantId}/v1/blueprints/history
    /// <summary>
    ///     Gets the blueprint application history for a tenant
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of blueprint history entries</returns>
    [HttpGet("history")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<BlueprintHistoryItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetHistory(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var history = await _blueprintHistory.GetHistoryAsync(tenantId, cancellationToken);

            var response = history.Select(h => new BlueprintHistoryItemDto
            {
                BlueprintId = h.BlueprintId.FullName,
                AppliedAt = h.AppliedAt,
                ApplicationMode = h.ApplicationMode.ToString(),
                PreviousVersion = h.PreviousVersion?.FullName,
                EntitiesCreated = h.EntitiesCreated,
                EntitiesUpdated = h.EntitiesUpdated,
                EntitiesDeleted = h.EntitiesDeleted,
                SeedDataChecksum = h.SeedDataChecksum
            });

            return Ok(response);
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

    // GET {tenantId}/v1/blueprints/current
    /// <summary>
    ///     Gets the current blueprint of a tenant
    /// </summary>
    /// <param name="blueprintName">
    ///     Optional blueprint name (without the version suffix). A tenant can host several
    ///     blueprints concurrently; without this the blueprint applied last is returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    ///     Current blueprint information, or 404 when the tenant has no blueprint at all - and,
    ///     when <paramref name="blueprintName" /> is given, also when that specific blueprint is
    ///     not installed, even though others are.
    /// </returns>
    [HttpGet("current")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintHistoryItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCurrent(
        [FromQuery] string? blueprintName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // A blank argument means "not provided" - the engine rejects a blank name as a
            // caller bug, which the generic catch below would turn into a 500. Trimming also
            // saves a name that arrived with surrounding whitespace from a copy-paste.
            var requestedBlueprint = NormalizeBlueprintName(blueprintName);

            var current = requestedBlueprint == null
                ? await _blueprintHistory.GetCurrentAsync(tenantId, cancellationToken)
                : await _blueprintHistory.GetCurrentByBlueprintNameAsync(
                    tenantId, requestedBlueprint, cancellationToken);

            if (current == null)
            {
                return NotFound();
            }

            var response = new BlueprintHistoryItemDto
            {
                BlueprintId = current.BlueprintId.FullName,
                AppliedAt = current.AppliedAt,
                ApplicationMode = current.ApplicationMode.ToString(),
                PreviousVersion = current.PreviousVersion?.FullName,
                EntitiesCreated = current.EntitiesCreated,
                EntitiesUpdated = current.EntitiesUpdated,
                EntitiesDeleted = current.EntitiesDeleted,
                SeedDataChecksum = current.SeedDataChecksum
            };

            return Ok(response);
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

    // GET {tenantId}/v1/blueprints/updates
    /// <summary>
    ///     Gets available blueprint updates for a tenant
    /// </summary>
    /// <param name="blueprintName">
    ///     Optional blueprint name (without the version suffix) to describe. Without it the
    ///     blueprint applied to the tenant last is described.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Update information</returns>
    [HttpGet("updates")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintUpdateInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAvailableUpdates(
        [FromQuery] string? blueprintName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var updateInfo = await _blueprintService.GetUpdateInfoAsync(
                tenantId, NormalizeBlueprintName(blueprintName), cancellationToken);

            var response = new BlueprintUpdateInfoDto
            {
                CurrentBlueprintId = updateInfo?.CurrentVersion.FullName,
                CurrentVersion = updateInfo?.CurrentVersion.Version.ToString(),
                RecommendedVersion = updateInfo?.RecommendedVersion?.FullName,
                HasUpdate = updateInfo?.RecommendedVersion != null,
                AvailableVersions = updateInfo?.AvailableVersions.Select(v => v.FullName).ToList() ?? []
            };

            return Ok(response);
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

    // POST {tenantId}/v1/blueprints/updates/preview
    /// <summary>
    ///     Previews a blueprint update without applying it
    /// </summary>
    /// <param name="request">Update request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of changes that would be applied</returns>
    [HttpPost("updates/preview")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintUpdatePreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PreviewUpdate(
        [FromBody] BlueprintUpdateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrEmpty(request.TargetVersion))
            {
                return BadRequest(new OperationFailedErrorDto("TargetVersion is required"));
            }

            var targetBlueprintId = new BlueprintId(request.TargetVersion);
            var updateMode = Enum.Parse<BlueprintUpdateMode>(request.UpdateMode, ignoreCase: true);

            var preview = await _blueprintService.PreviewUpdateAsync(
                tenantId,
                targetBlueprintId,
                updateMode,
                cancellationToken);

            return Ok(BlueprintUpdatePreviewMapper.ToDto(preview, request.TargetVersion));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
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

    // POST {tenantId}/v1/blueprints/updates/apply
    /// <summary>
    ///     Applies a blueprint update to the tenant
    ///     <para>
    ///         Tenant upgrade guard (CK v2 F2.5, AB#6871): when the CK models the update installs or raises would break
    ///         an installed model the request is refused with <c>409 CK_UPGRADE_BLOCKED</c> before the first change; the
    ///         installation record, the CK models and the runtime data stay as they were.
    ///         <c>ForceBreakingUpdate = true</c> (data model management policy) applies anyway and logs the blockers and
    ///         the caller at WARN. A dry run never refuses: it answers 200 with <c>isBlocked</c> and the blockers.
    ///     </para>
    /// </summary>
    /// <param name="request">Update request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    ///     The update result (AB#6315). It lists every attribute the seed would have blanked and
    ///     whether the tenant value was kept (default) or blanked (explicit confirmation via
    ///     <c>allowBlanking</c> / <c>confirmedBlankings</c>).
    /// </returns>
    [HttpPost("updates/apply")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(BlueprintUpdateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ApplyUpdate(
        [FromBody] BlueprintUpdateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrEmpty(request.TargetVersion))
            {
                return BadRequest(new OperationFailedErrorDto("TargetVersion is required"));
            }

            var targetBlueprintId = new BlueprintId(request.TargetVersion);
            var updateMode = Enum.Parse<BlueprintUpdateMode>(request.UpdateMode, ignoreCase: true);

            if (request.ForceBreakingUpdate && !await MayForceBreakingUpdateAsync())
            {
                return Forbid();
            }

            var options = new BlueprintUpdateOptions
            {
                DryRun = request.DryRun,
                AllowBlanking = request.AllowBlanking,
                ConfirmedBlankings = BlueprintUpdatePreviewMapper.ToConfirmations(request.ConfirmedBlankings),
                CkUpgradeGuard = GuardMode(request.ForceBreakingUpdate)
            };

            if (request.ConflictResolutions != null)
            {
                options.ConflictResolutions = new Dictionary<string, ConflictResolution>();
                foreach (var resolution in request.ConflictResolutions)
                {
                    options.ConflictResolutions[resolution.Key] =
                        Enum.Parse<ConflictResolution>(resolution.Value, ignoreCase: true);
                }
            }

            var result = await _blueprintService.ApplyUpdateAsync(
                tenantId,
                targetBlueprintId,
                updateMode,
                options,
                cancellationToken);

            if (!result.Success)
            {
                if (result.IsCkUpgradeBlocked)
                {
                    return CkUpgradeBlockedError.CreateBlueprintResult(targetBlueprintId.FullName, result.CkUpdateSet,
                        BlueprintUpdatePreviewMapper.ToDtos(result.CkUpgradeBlockers));
                }

                var failure = string.Join(", ", result.Errors.Count > 0 ? result.Errors : ["Update failed"]);
                return result.CkUpgradeGuardFailed
                    ? StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(failure))
                    : BadRequest(new OperationFailedErrorDto(failure));
            }

            if (!request.DryRun)
            {
                LogForcedBreakingUpdate(tenantId, targetBlueprintId.FullName, result.CkUpdateSet,
                    result.CkUpgradeBlockers);
            }

            return Ok(BlueprintUpdatePreviewMapper.ToDto(result));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
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

    /// <summary>
    ///     AB#6871: the guard mode of a request. Every REST apply is guarded; the caller may only override it with
    ///     <c>ForceBreakingUpdate</c> (policy checked by <see cref="MayForceBreakingUpdateAsync" />).
    /// </summary>
    private static BlueprintCkUpgradeGuardMode GuardMode(bool forceBreakingUpdate) =>
        forceBreakingUpdate ? BlueprintCkUpgradeGuardMode.ForceBreakingUpdate : BlueprintCkUpgradeGuardMode.Enforce;

    /// <summary>
    ///     AB#6871: overriding the tenant upgrade guard is a data model operation, like importing a CK model - the
    ///     blueprint endpoints themselves only need the tenant read-write policy.
    /// </summary>
    private async Task<bool> MayForceBreakingUpdateAsync()
    {
        var authorization = await _authorizationService.AuthorizeAsync(HttpContext.User, null,
            AssetRepositoryServiceConstants.DataModelManagementPolicy);
        return authorization.Succeeded;
    }

    /// <summary>
    ///     AB#6871: audit line of a forced breaking update - the blockers the caller forced past and who did it. The
    ///     engine returns blockers on a successful result only when the guard let them pass.
    /// </summary>
    private void LogForcedBreakingUpdate(string tenantId, string blueprintId, IReadOnlyList<string> updateSet,
        IReadOnlyList<BlueprintCkUpgradeBlocker> blockers)
    {
        if (blockers.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Forced breaking CK update on tenant {TenantId} by {Caller}: blueprint {BlueprintId} installs {UpdateSet} and breaks {BlockerCount} installed model(s): {Blockers}",
            tenantId, CallerDescription.From(HttpContext.User), blueprintId, string.Join(", ", updateSet),
            blockers.Count, string.Join("; ", blockers.Select(b => $"{b.Dependent} [{b.Kind} {b.Subject}] by {b.Update}")));
    }

    /// <summary>
    ///     Maps a blank <c>blueprintName</c> argument to <c>null</c> ("not provided") and trims
    ///     the rest, so the optional parameter stays optional however the client sends it.
    /// </summary>
    private static string? NormalizeBlueprintName(string? blueprintName)
    {
        return string.IsNullOrWhiteSpace(blueprintName) ? null : blueprintName.Trim();
    }

    // GET {tenantId}/v1/blueprints/installations
    /// <summary>
    ///     Lists all blueprints currently installed on the tenant. Distinct from
    ///     the application history (which is append-only).
    /// </summary>
    [HttpGet("installations")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<BlueprintInstallationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInstallations(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var rows = await _blueprintInstallations.GetInstalledAsync(tenantId, cancellationToken);

            var response = rows.Select(r => new BlueprintInstallationDto
            {
                BlueprintId = r.BlueprintId.FullName,
                InstalledAt = r.InstalledAt,
                LastUpdatedAt = r.LastUpdatedAt,
                IsDependency = r.IsDependency,
                ResolvedDependencies = r.ResolvedDependencies.Select(d => d.FullName).ToList(),
                SeedDataChecksum = r.SeedDataChecksum
            });

            return Ok(response);
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

    // DELETE {tenantId}/v1/blueprints/{blueprintName}?cascade=true
    /// <summary>
    ///     Uninstalls a blueprint from the tenant. With cascade=true, dependents
    ///     are uninstalled first and orphaned dependencies are auto-cleaned.
    /// </summary>
    /// <param name="blueprintName">Name of the blueprint to remove (without version).</param>
    /// <param name="cascade">When true, also remove blueprints that depend on the target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("{blueprintName}")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(BlueprintUninstallResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BlueprintUninstallResultDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Uninstall(
        [Required] string blueprintName,
        [FromQuery] bool cascade = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrEmpty(blueprintName))
            {
                return BadRequest(new OperationFailedErrorDto("BlueprintName is required"));
            }

            var result = await _blueprintService.UninstallAsync(
                tenantId, blueprintName, cascade, cancellationToken);

            var response = new BlueprintUninstallResultDto
            {
                Success = result.Success,
                UninstalledBlueprintId = result.UninstalledBlueprintId?.FullName,
                EntitiesDeleted = result.EntitiesDeleted,
                CascadedDependencies = result.CascadedDependencies.Select(d => d.FullName).ToList(),
                BlockingDependents = result.BlockingDependents.Select(d => d.FullName).ToList(),
                Warnings = result.Warnings.ToList()
            };

            if (result.Success)
            {
                return Ok(response);
            }

            // Blocked by dependents → 409 Conflict (caller can retry with cascade=true).
            if (result.BlockingDependents.Count > 0)
            {
                return Conflict(response);
            }

            // Target not installed → 404.
            if (result.UninstalledBlueprintId == null)
            {
                return NotFound();
            }

            return BadRequest(new OperationFailedErrorDto(
                string.Join(", ", result.Errors.Count > 0 ? result.Errors : ["Uninstall failed"])));
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
}
