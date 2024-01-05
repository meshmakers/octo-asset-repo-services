using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for management of large binaries
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
// ReSharper disable once ClassNeverInstantiated.Global
public class LargeBinariesController
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

    // GET system/v1/largeBinaries
    /// <summary>
    ///     Downloads are large binary with given tenantId and large binary id
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadOnlyPolicy)]
    public async Task<IActionResult> Get([FromQuery] string tenantId, [FromQuery] string largeBinaryId)
    {
        var tenantContext = await _octoService.SystemContext.GetChildTenantContextAsync(tenantId);
        var repository = tenantContext.GetTenantRepository();
        var streamHandler = await repository.DownloadLargeBinaryAsync(OctoObjectId.Parse(largeBinaryId));

        return new FileStreamResult(streamHandler.Stream, streamHandler.ContentType);
    }
}