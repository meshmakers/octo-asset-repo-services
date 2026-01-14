using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
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
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<TenantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] PagingParams? pagingParams)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            var result =
                await _octoService.SystemContext.GetChildTenantsAsync(session, pagingParams?.Skip, pagingParams?.Take);

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
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/tenants/{id}
    /// <summary>
    ///     Returns client information based on its client id
    /// </summary>
    /// <param name="id">ID of the client</param>
    /// <returns>An Object that describes the client.</returns>
    [HttpGet("{id}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([Required] string id)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            if (!await _octoService.SystemContext.IsChildTenantExistingAsync(session, id))
            {
                return NotFound();
            }

            var octoTenant = await _octoService.SystemContext.GetChildTenantAsync(session, id);
            await session.CommitTransactionAsync();
            return Ok(CreateTenantDto(octoTenant));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: system/v1/tenants?tenantId=abc&databaseName=xyz&blueprintId=MyBlueprint-1.0.0
    /// <summary>
    ///     Creates new tenants, optionally with a blueprint applied
    /// </summary>
    /// <param name="tenantId">ID of tenant</param>
    /// <param name="databaseName">Name of the database</param>
    /// <param name="blueprintId">Optional blueprint ID to apply (e.g., "MyBlueprint-1.0.0")</param>
    /// <returns></returns>
    [HttpPost]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Post(
        [Required] string tenantId,
        [Required] string databaseName,
        string? blueprintId = null)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            if (!string.IsNullOrEmpty(blueprintId))
            {
                var bpId = new BlueprintId(blueprintId);
                var result = await _octoService.SystemContext.CreateChildTenantAsync(session, databaseName, tenantId, bpId);

                if (result != null && !result.IsSuccess)
                {
                    await session.AbortTransactionAsync();
                    var messages = result.OperationResult?.Messages?.Select(m => m.MessageText) ?? [];
                    return BadRequest(new OperationFailedErrorDto(
                        $"Blueprint application failed: {string.Join(", ", messages)}"));
                }
            }
            else
            {
                await _octoService.SystemContext.CreateChildTenantAsync(session, databaseName, tenantId);
            }

            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: system/v1/tenants/attach?tenantId=abc&databaseName=xyz
    /// <summary>
    ///     Appends an existing database as tenant
    /// </summary>
    /// <param name="tenantId">ID tenant</param>
    /// <param name="databaseName">Name of the database (have to exist)</param>
    /// <returns></returns>
    [HttpPost("attach")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Attach([Required] string tenantId, [Required] string databaseName)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            await _octoService.SystemContext.AttachChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: system/v1/tenants/detach?tenantId=abc&databaseName=xyz
    /// <summary>
    ///     Appends an existing database as tenant
    /// </summary>
    /// <param name="tenantId">ID of tenant</param>
    /// <returns></returns>
    [HttpPost("detach")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Detach([Required] string tenantId)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            await _octoService.SystemContext.DetachChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return NoContent();
        }
        catch (PersistenceException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // PUT: system/v1/tenants/clear?tenantId=abc
    /// <summary>
    ///     Clears the content of a tenant
    /// </summary>
    /// <param name="tenantId">Name of tenant</param>
    /// <returns></returns>
    [HttpPut("clear")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Clear([Required] string tenantId)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            await _octoService.SystemContext.ClearChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // PUT: system/v1/tenants/update?tenantId=abc
    /// <summary>
    ///     Updates the system schema
    /// </summary>
    /// <param name="tenantId">Name of tenant</param>
    /// <returns></returns>
    [HttpPut("update")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Update([Required] string tenantId)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            // TODO: Implement dispose
            // await _octoService.SystemContext.UpdateTenantSystemCkModelAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return BadRequest(new OperationFailedErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // PUT: system/v1/tenants/clearCache?tenantId=abc
    /// <summary>
    ///     Clears the caches of a tenant
    /// </summary>
    /// <param name="tenantId">ID of tenant</param>
    /// <returns></returns>
    [HttpPut("clearCache")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearCache([Required] string tenantId)
    {
        try
        {
            var correlationId = Guid.NewGuid();
            await _distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId, correlationId, DateTime.Now));
            await Task.Delay(2000);
            await _distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId, correlationId, DateTime.Now));

            return Ok("Cache cleared");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // DELETE: system/v1/tenants/delete?tenantId=abc
    /// <summary>
    ///     Deletes a tenant
    /// </summary>
    /// <param name="tenantId">ID of tenant</param>
    /// <returns></returns>
    [HttpDelete]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Delete([Required] string tenantId)
    {
        try
        {
            using var session = await _octoService.SystemContext.GetAdminSessionAsync();
            session.StartTransaction();

            await _octoService.SystemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return Ok();
        }
        catch (TenantException e)
        {
            return NotFound(e.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
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