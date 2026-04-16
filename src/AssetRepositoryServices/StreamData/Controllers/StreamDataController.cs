using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using GraphQL;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST Controller for stream data management
/// </summary>

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class StreamDataController : ControllerBase
{
    private readonly ILogger<StreamDataController> _logger;
    private readonly ISystemContext _systemContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public StreamDataController(ILogger<StreamDataController> logger, ISystemContext systemContext)
    {
        _logger = logger;
        _systemContext = systemContext;
    }

    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    [HttpPost("enable")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
    }

    /// <summary>
    /// Disables stream data for a given tenant
    /// </summary>
    [HttpPost("disable")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
    }
}
