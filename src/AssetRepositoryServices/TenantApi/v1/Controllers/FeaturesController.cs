using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     Aggregate tenant-capability status for the Refinery Studio Tenant Features panel (AB#4884).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/features")]
[ApiVersion("1.0")]
public class FeaturesController : ControllerBase
{
    private readonly ISystemContext _systemContext;
    private readonly ITenantCapabilityStateReader _capabilityStateReader;
    private readonly IOptions<StreamDataInstanceConfiguration> _streamDataInstanceConfiguration;

    /// <summary>
    /// Constructor
    /// </summary>
    public FeaturesController(
        ISystemContext systemContext,
        ITenantCapabilityStateReader capabilityStateReader,
        IOptions<StreamDataInstanceConfiguration> streamDataInstanceConfiguration)
    {
        _systemContext = systemContext;
        _capabilityStateReader = capabilityStateReader;
        _streamDataInstanceConfiguration = streamDataInstanceConfiguration;
    }

    /// <summary>
    ///     Returns the enabled state of the four capabilities the tenant delete/detach guard evaluates
    ///     (AB#4255), read through the same <see cref="ITenantCapabilityStateReader" /> — so the panel
    ///     and the guard never disagree. Read failures propagate as 500: an unreadable state must not
    ///     be shown as "disabled" while the guard would still refuse.
    /// </summary>
    /// <param name="tenantId">Tenant whose capability flags are reported.</param>
    [HttpGet("status")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public async Task<ActionResult<TenantFeaturesStatusDto>> Status([Required] string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var enabled = await _capabilityStateReader.GetEnabledCapabilitiesAsync(tenantContext);

        return Ok(new TenantFeaturesStatusDto
        {
            StreamData = new StreamDataFeatureStatusDto
            {
                InstanceEnabled = _streamDataInstanceConfiguration.Value.Enabled,
                TenantEnabled = enabled.Contains(TenantCapability.StreamData),
            },
            Communication = new TenantFeatureStatusDto
            {
                TenantEnabled = enabled.Contains(TenantCapability.Communication),
            },
            Reporting = new TenantFeatureStatusDto
            {
                TenantEnabled = enabled.Contains(TenantCapability.Reporting),
            },
            AiServices = new TenantFeatureStatusDto
            {
                TenantEnabled = enabled.Contains(TenantCapability.AiServices),
            },
        });
    }
}
