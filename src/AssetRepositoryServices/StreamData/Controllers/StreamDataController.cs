using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using GraphQL;
using IdentityModel;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST Controller for stream data management
/// </summary>

[Microsoft.AspNetCore.Authorization.Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class StreamDataController : ControllerBase
{
    private readonly ILogger<StreamDataController> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IOptions<StreamDataInstanceConfiguration> _instanceConfiguration;

    /// <summary>
    /// Constructor
    /// </summary>
    public StreamDataController(
        ILogger<StreamDataController> logger,
        ISystemContext systemContext,
        IOptions<StreamDataInstanceConfiguration> instanceConfiguration)
    {
        _logger = logger;
        _systemContext = systemContext;
        _instanceConfiguration = instanceConfiguration;
    }

    /// <summary>
    /// Returns the StreamData feature availability flags for the current tenant. Exposed to the
    /// Refinery Studio so it can decide whether to render the archives navigation at all
    /// (instance-level) and whether tenants need to opt in (tenant-level). Concept §6.
    /// </summary>
    /// <param name="tenantId">Tenant whose flag is reported.</param>
    [HttpGet("status")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<ActionResult<StreamDataStatusDto>> Status([Required] string tenantId)
    {
        var instanceEnabled = _instanceConfiguration.Value.Enabled;

        var tenantEnabled = false;
        if (instanceEnabled)
        {
            try
            {
                var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
                tenantEnabled = await tenantContext.IsStreamDataEnabledAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StreamData status: failed to read tenant flag for '{TenantId}'; defaulting to false.",
                    tenantId);
            }
        }

        return Ok(new StreamDataStatusDto
        {
            InstanceEnabled = instanceEnabled,
            TenantEnabled = tenantEnabled,
        });
    }

    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    [HttpPost("enable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
        catch (StreamDataException e)
        {
            // Concept §12: known stream-data failures (e.g. instance-level disabled, archive
            // path invalid, activation failed) — return the message text only, no stack trace.
            _logger.LogWarning("EnableStreamData refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Disables stream data for a given tenant
    /// </summary>
    [HttpPost("disable")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
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
        catch (StreamDataException e)
        {
            _logger.LogWarning("DisableStreamData refused for tenant '{TenantId}': {Reason}", tenantId, e.Message);
            return BadRequest(e.Message);
        }
    }
}
