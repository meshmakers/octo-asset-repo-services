using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="blueprintHistory">Blueprint history service</param>
    /// <param name="blueprintService">Blueprint service</param>
    /// <param name="blueprintInstallations">Tenant blueprint installations service</param>
    public BlueprintsController(
        ITenantBlueprintHistory blueprintHistory,
        IBlueprintService blueprintService,
        ITenantBlueprintInstallations blueprintInstallations)
    {
        _blueprintHistory = blueprintHistory;
        _blueprintService = blueprintService;
        _blueprintInstallations = blueprintInstallations;
    }

    // POST {tenantId}/v1/blueprints/apply
    /// <summary>
    ///     Applies a blueprint to the tenant for the first time. CK models are
    ///     loaded into the tenant and seed data is imported via upsert.
    /// </summary>
    /// <param name="request">Apply request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Apply result with summary of changes</returns>
    [HttpPost("apply")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(BlueprintApplyResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
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

            var result = await _blueprintService.ApplyBlueprintAsync(
                tenantId,
                blueprintId,
                request.Force,
                cancellationToken);

            if (!result.IsSuccess)
            {
                var errors = result.OperationResult.Messages
                    .Where(m => m.MessageLevel == MessageLevel.Error)
                    .Select(m => m.MessageText)
                    .ToList();

                return BadRequest(new OperationFailedErrorDto(
                    string.Join(", ", errors.Count > 0 ? errors : ["Blueprint apply failed"])));
            }

            var response = new BlueprintApplyResultDto
            {
                Success = true,
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current blueprint information or 404 if no blueprint is applied</returns>
    [HttpGet("current")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintHistoryItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCurrent(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var current = await _blueprintHistory.GetCurrentAsync(tenantId, cancellationToken);

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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Update information</returns>
    [HttpGet("updates")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintUpdateInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAvailableUpdates(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var updateInfo = await _blueprintService.GetUpdateInfoAsync(tenantId, cancellationToken);

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

            var response = new BlueprintUpdatePreviewDto
            {
                TargetVersion = request.TargetVersion,
                EntitiesToAdd = preview.EntitiesToAdd,
                EntitiesToUpdate = preview.EntitiesToUpdate,
                EntitiesToDelete = preview.EntitiesToDelete,
                Conflicts = preview.Conflicts.Select(c => new BlueprintConflictDto
                {
                    EntityId = c.EntityId,
                    Description = c.Description,
                    SuggestedResolution = c.SuggestedResolution.ToString()
                }).ToList(),
                Warnings = preview.Warnings.ToList()
            };

            return Ok(response);
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
    /// </summary>
    /// <param name="request">Update request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpPost("updates/apply")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
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

            var options = new BlueprintUpdateOptions
            {
                DryRun = request.DryRun
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
                return BadRequest(new OperationFailedErrorDto(
                    string.Join(", ", result.Errors.Count > 0 ? result.Errors : ["Update failed"])));
            }

            return NoContent();
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
