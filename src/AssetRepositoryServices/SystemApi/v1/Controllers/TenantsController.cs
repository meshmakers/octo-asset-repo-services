using System.ComponentModel.DataAnnotations;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for tenants management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class TenantsController : ControllerBase
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly IOctoService _octoService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService">Octo service for tenant management</param>
    /// <param name="distributionEventHubService"></param>
    public TenantsController(IOctoService octoService, IDistributionEventHubService distributionEventHubService)
    {
        _octoService = octoService;
        _distributionEventHubService = distributionEventHubService;
    }

    // GET system/v1/tenants
    /// <summary>
    ///     Returns all existing tenants using pages
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadOnlyPolicy)]
    public async Task<IActionResult> Get([FromQuery] PagingParams? pagingParams)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        var result = await _octoService.SystemContext.GetChildTenantsAsync(session, pagingParams?.Skip, pagingParams?.Take);

        if (pagingParams != null)
        {
            var pagedResult = new PagedResult<TenantDto>(result.Items.Select(CreateTenantDto),
                pagingParams.Skip, pagingParams.Take, result.TotalCount);

            Response.Headers.Append("X-Pagination", pagedResult.GetHeader().ToJson());

            return Ok(pagedResult);
        }

        await session.CommitTransactionAsync();

        return Ok(result.Items.Select(CreateTenantDto));
    }

    // GET system/v1/tenants/{id}
    /// <summary>
    ///     Returns client information based on it's client id
    /// </summary>
    /// <param name="id">Id of the client</param>
    /// <returns>An Object that describes the client.</returns>
    [HttpGet("{id}")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadOnlyPolicy)]
    public async Task<IActionResult> Get([Required] string id)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        if (!await _octoService.SystemContext.IsChildTenantExistingAsync(session, id))
        {
            return NotFound();
        }

        var octoTenant = await _octoService.SystemContext.GetChildTenantAsync(session, id);
        await session.CommitTransactionAsync();
        return Ok(CreateTenantDto(octoTenant));
    }

    // POST: system/v1/tenants?tenantId=abc&databaseName=xyz
    /// <summary>
    ///     Creates new tenants
    /// </summary>
    /// <param name="tenantId">Id of tenant</param>
    /// <param name="databaseName">Name of database</param>
    /// <returns></returns>
    [HttpPost]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Post([Required] string tenantId, [Required] string databaseName)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            await _octoService.SystemContext.CreateChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return Conflict(e.Message);
        }
    }

    // POST: system/v1/tenants/attach?tenantId=abc&databaseName=xyz
    /// <summary>
    ///     Appends an existing database as tenant
    /// </summary>
    /// <param name="tenantId">Id of tenant</param>
    /// <param name="databaseName">Name of database (have to exist)</param>
    /// <returns></returns>
    [HttpPost("attach")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Attach([Required] string tenantId, [Required] string databaseName)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            await _octoService.SystemContext.AttachChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return Conflict(e.Message);
        }
    }

    // POST: system/v1/tenants/detach?tenantId=abc&databaseName=xyz
    /// <summary>
    ///     Appends an existing database as tenant
    /// </summary>
    /// <param name="tenantId">Id of tenant</param>
    /// <returns></returns>
    [HttpPost("detach")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Detach([Required] string tenantId)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            await _octoService.SystemContext.DetachChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return Conflict(e.Message);
        }
    }

    // PUT: system/v1/tenants/clear?tenantId=abc
    /// <summary>
    ///     Clears the content of a tenant
    /// </summary>
    /// <param name="tenantId">Name of tenant</param>
    /// <returns></returns>
    [HttpPut("clear")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Clear([Required] string tenantId)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            await _octoService.SystemContext.ClearChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
        }
    }

    // PUT: system/v1/tenants/update?tenantId=abc
    /// <summary>
    ///     Updates the system schema
    /// </summary>
    /// <param name="tenantId">Name of tenant</param>
    /// <returns></returns>
    [HttpPut("update")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Update([Required] string tenantId)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            // TODO: Implement dispose
            // await _octoService.SystemContext.UpdateTenantSystemCkModelAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
        }
    }

    // PUT: system/v1/tenants/clearCache?tenantId=abc
    /// <summary>
    ///     Clears the caches of a tenant
    /// </summary>
    /// <param name="tenantId">ID of tenant</param>
    /// <returns></returns>
    [HttpPut("clearCache")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> ClearCache([Required] string tenantId)
    {
        try
        {
            await _distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId));
            await _distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId));

            return Ok("Cache cleared");
        }
        catch (Exception e)
        {
            return Conflict(e.Message);
        }
    }

    // DELETE: system/v1/tenants/delete?tenantId=abc
    /// <summary>
    ///     Deletes a tenant
    /// </summary>
    /// <param name="tenantId">Id of tenant</param>
    /// <returns></returns>
    [HttpDelete]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Delete([Required] string tenantId)
    {
        using var session = await _octoService.SystemContext.GetSystemSessionAsync();
        session.StartTransaction();

        try
        {
            await _octoService.SystemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return NotFound(e.Message);
        }
    }

    private TenantDto CreateTenantDto(OctoTenant octoTenant)
    {
        var tenantDto = new TenantDto
        {
            TenantId = octoTenant.TenantId,
            Database = octoTenant.DatabaseName
        };
        return tenantDto;
    }
}