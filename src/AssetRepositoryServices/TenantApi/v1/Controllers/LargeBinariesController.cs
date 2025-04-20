using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get( [FromQuery] string largeBinaryId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        var tenantRepository = await _octoService.SystemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session, OctoObjectId.Parse(largeBinaryId));

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return new FileStreamResult(streamHandler.Stream, streamHandler.ContentType);
    }
}