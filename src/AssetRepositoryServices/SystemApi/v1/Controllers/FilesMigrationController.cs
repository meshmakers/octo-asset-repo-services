using System.Security.Claims;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     Pre-check of the move of System.Reporting file data to System.Files (AB#6171 S3, AB#6175).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/files")]
[ApiController]
[ApiVersion("1.0")]
public class FilesMigrationController : ControllerBase
{
    private readonly FilesMigrationStatusService _statusService;
    private readonly ISystemContext _systemContext;
    private readonly ILogger<FilesMigrationController> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    public FilesMigrationController(FilesMigrationStatusService statusService, ISystemContext systemContext,
        ILogger<FilesMigrationController> logger)
    {
        _statusService = statusService;
        _systemContext = systemContext;
        _logger = logger;
    }

    // GET system/v1/files/migration-status/{tenantId}
    /// <summary>
    ///     Pre-check report of the file data migration of one tenant: legacy System.Reporting file entities per
    ///     type, legacy association type fields and GridFS stamps, orphans without a parent, every entity outside
    ///     the file collections that names a System.Reporting file type literally (pipelines, policies, queries,
    ///     UI elements, …) with its blueprint source, and the latest sweep audit records. Read-only. The default
    ///     (<c>mode=Full</c>) scans every entity collection of the tenant and can take minutes on a large tenant
    ///     (longer than the 180 s ingress limit); <c>mode=Light</c> (AB#6860) answers from stored state in
    ///     seconds — counts, audit records, sweep state and pre-check state — without orphans and literal references.
    /// </summary>
    /// <remarks>
    ///     The report crosses tenant boundaries, so besides the system read scope the caller must be signed in to
    ///     the system tenant (<c>tenant_id</c> claim) and, with a user token, hold the role
    ///     <c>AdminPanelManagement</c>.
    /// </remarks>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="mode"><c>Full</c> (default) or <c>Light</c> (no scan; stored state only).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("migration-status/{tenantId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FilesMigrationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetMigrationStatus(string tenantId,
        [FromQuery] FilesMigrationStatusMode mode = FilesMigrationStatusMode.Full,
        CancellationToken cancellationToken = default)
    {
        if (!IsSystemAdministrator(User))
        {
            return Forbid();
        }

        try
        {
            var status = await _statusService.GetStatusAsync(tenantId, mode, cancellationToken);
            if (status == null)
            {
                return NotFound(new NotFoundErrorDto($"Tenant '{tenantId}' not found"));
            }

            return Ok(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Files migration status of tenant '{TenantId}' failed", tenantId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new InternalServerErrorDto("The files migration status could not be built; see the service log."));
        }
    }

    /// <summary>
    ///     Signed in to the system tenant; user tokens additionally need the admin panel role.
    /// </summary>
    internal bool IsSystemAdministrator(ClaimsPrincipal user)
    {
        return SystemAdministratorGuard.IsSystemAdministrator(user, _systemContext);
    }
}
