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
    private const string TenantIdClaimType = "tenant_id";

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
    ///     UI elements, …) with its blueprint source, and the latest sweep audit records. Read-only; scans every
    ///     entity collection of the tenant.
    /// </summary>
    /// <remarks>
    ///     The report crosses tenant boundaries, so besides the system read scope the caller must be signed in to
    ///     the system tenant (<c>tenant_id</c> claim) and, with a user token, hold the role
    ///     <c>AdminPanelManagement</c>.
    /// </remarks>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("migration-status/{tenantId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FilesMigrationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetMigrationStatus(string tenantId, CancellationToken cancellationToken = default)
    {
        if (!IsSystemAdministrator(User))
        {
            return Forbid();
        }

        try
        {
            var status = await _statusService.GetStatusAsync(tenantId, cancellationToken);
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
        var tokenTenantId = user.FindFirstValue(TenantIdClaimType);
        if (string.IsNullOrEmpty(tokenTenantId) ||
            tokenTenantId.NormalizeString() != _systemContext.TenantId.NormalizeString())
        {
            return false;
        }

        var isUserToken = user.FindFirst("sub") != null || user.FindFirst(ClaimTypes.NameIdentifier) != null;
        return !isUserToken ||
               user.Identities.SelectMany(i => i.FindAll(i.RoleClaimType).Concat(i.FindAll("role")))
                   .Any(c => c.Value == CommonConstants.AdminPanelManagementRole);
    }
}
