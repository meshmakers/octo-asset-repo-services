using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using GraphQL;
using MassTransit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;
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
    private readonly ITenantManager _tenantManager;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="tenantManager"></param>
    public StreamDataController(ILogger<StreamDataController> logger, ITenantManager tenantManager)
    {
        _logger = logger;
        _tenantManager = tenantManager;
    }
    
    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    [HttpPost("enable")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public async Task<IActionResult> Enable([Required] string tenantId)
    {
        try
        {
            await _tenantManager.EnableStreamData(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }
    
    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    [HttpPost("disable")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    public async Task<IActionResult> Disable([Required] string tenantId)
    {
        try
        {
            await _tenantManager.DisableStreamDataAsync(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }
}