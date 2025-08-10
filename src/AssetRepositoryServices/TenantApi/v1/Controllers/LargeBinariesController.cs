using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-specific access to large binaries
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
// ReSharper disable once ClassNeverInstantiated.Global
public class LargeBinariesController : ControllerBase
{
    private readonly IOctoService _octoService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService">Octo service for tenant management</param>
    public LargeBinariesController(IOctoService octoService)
    {
        _octoService = octoService;
    }

    // GET {tenantId}/v1/largeBinaries
    /// <summary>
    ///     Downloads are large binary with given tenantId and large binary id
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] string largeBinaryId)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
            }

            if (string.IsNullOrEmpty(largeBinaryId))
            {
                return BadRequest(new InternalServerErrorDto("LargeBinaryId is required"));
            }

            var tenantRepository = await _octoService.SystemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();

            var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session, OctoObjectId.Parse(largeBinaryId));
            if (streamHandler.Stream == null)
            {
                return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
            }

            await session.CommitTransactionAsync().ConfigureAwait(false);

            return new FileStreamResult(streamHandler.Stream, streamHandler.ContentType);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
        catch (FormatException ex)
        {
            return BadRequest(new InternalServerErrorDto($"Invalid largeBinaryId format: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }
}