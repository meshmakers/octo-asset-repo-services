using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
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
    private readonly ITenantBackupService _backupService;
    private readonly IBlueprintService _blueprintService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="blueprintHistory">Blueprint history service</param>
    /// <param name="backupService">Backup service</param>
    /// <param name="blueprintService">Blueprint service</param>
    public BlueprintsController(
        ITenantBlueprintHistory blueprintHistory,
        ITenantBackupService backupService,
        IBlueprintService blueprintService)
    {
        _blueprintHistory = blueprintHistory;
        _backupService = backupService;
        _blueprintService = blueprintService;
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
                CreateBackup = request.CreateBackup,
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

    // GET {tenantId}/v1/blueprints/backups
    /// <summary>
    ///     Lists all backups for a tenant
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of backups</returns>
    [HttpGet("backups")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<BlueprintBackupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetBackups(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var backups = await _backupService.ListBackupsAsync(tenantId, cancellationToken);

            var response = backups.Select(b => new BlueprintBackupDto
            {
                BackupId = b.BackupId,
                CreatedAt = b.CreatedAt,
                BlueprintId = b.BlueprintVersion ?? string.Empty,
                Reason = b.Reason ?? string.Empty,
                SizeBytes = b.SizeBytes
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

    // POST {tenantId}/v1/blueprints/backups/{backupId}/restore
    /// <summary>
    ///     Restores a backup
    /// </summary>
    /// <param name="backupId">Backup ID to restore</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Restore result</returns>
    [HttpPost("backups/{backupId}/restore")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(BlueprintRestoreResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RestoreBackup(
        [Required] string backupId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrEmpty(backupId))
            {
                return BadRequest(new OperationFailedErrorDto("BackupId is required"));
            }

            var result = await _backupService.RestoreBackupAsync(tenantId, backupId, cancellationToken);

            if (!result.Success)
            {
                return BadRequest(new OperationFailedErrorDto(
                    string.Join(", ", result.Errors.Count > 0 ? result.Errors : ["Restore failed"])));
            }

            var response = new BlueprintRestoreResultDto
            {
                Success = result.Success,
                EntitiesRestored = result.EntitiesRestored,
                Messages = result.Warnings.ToList()
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
}
