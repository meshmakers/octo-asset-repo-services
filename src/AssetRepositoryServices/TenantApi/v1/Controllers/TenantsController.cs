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

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-scoped child tenant management.
///     Each tenant can manage its own child tenants through this API.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class TenantsController : ControllerBase
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly IOctoService _octoService;

    /// <summary>
    ///     Constructor
    /// </summary>
    public TenantsController(IOctoService octoService, IDistributionEventHubService distributionEventHubService)
    {
        _octoService = octoService;
        _distributionEventHubService = distributionEventHubService;
    }

    private async Task<ITenantContext?> GetTenantContextAsync()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return null;
        }

        return await _octoService.SystemContext.TryFindTenantContextAsync(tenantId);
    }

    // GET {tenantId}/v1/tenants
    /// <summary>
    ///     Returns all child tenants of the current tenant using pages
    /// </summary>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<TenantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] PagingParams? pagingParams)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            var result = await tenantContext.GetChildTenantsAsync(session, pagingParams?.Skip, pagingParams?.Take);

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

    // GET {tenantId}/v1/tenants/{id}
    /// <summary>
    ///     Returns a child tenant by its tenant ID
    /// </summary>
    /// <param name="id">ID of the child tenant</param>
    [HttpGet("{id}")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([Required] string id)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            if (!await tenantContext.IsChildTenantExistingAsync(session, id))
            {
                return NotFound();
            }

            var octoTenant = await tenantContext.GetChildTenantAsync(session, id);
            await session.CommitTransactionAsync();
            return Ok(CreateTenantDto(octoTenant));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/tenants?tenantId=abc&databaseName=xyz&blueprintId=MyBlueprint-1.0.0
    /// <summary>
    ///     Creates a new child tenant, optionally with a blueprint applied
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant to create</param>
    /// <param name="databaseName">Name of the database</param>
    /// <param name="blueprintId">Optional blueprint ID to apply</param>
    [HttpPost]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Post(
        [Required] string childTenantId,
        [Required] string databaseName,
        string? blueprintId = null)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            try
            {
                if (!string.IsNullOrEmpty(blueprintId))
                {
                    var bpId = new BlueprintId(blueprintId);
                    var result = await tenantContext.CreateChildTenantAsync(session, databaseName, childTenantId, bpId);

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
                    await tenantContext.CreateChildTenantAsync(session, databaseName, childTenantId);
                }

                await session.CommitTransactionAsync();
            }
            catch
            {
                // Abort so the octosystem tenant entries inserted in this transaction are rolled
                // back (AB#1958). The engine has already dropped the tenant database/user and
                // written the failure to the event log.
                try
                {
                    await session.AbortTransactionAsync();
                }
                catch
                {
                    // The driver may have already aborted the transaction - ignore.
                }

                throw;
            }

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

    // POST: {tenantId}/v1/tenants/attach?childTenantId=abc&databaseName=xyz
    /// <summary>
    ///     Attaches an existing database as a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    /// <param name="databaseName">Name of the database (must exist)</param>
    [HttpPost("attach")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Attach([Required] string childTenantId, [Required] string databaseName)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.AttachChildTenantAsync(session, databaseName, childTenantId);
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

    // POST: {tenantId}/v1/tenants/detach?childTenantId=abc
    /// <summary>
    ///     Detaches a child tenant (keeps the database)
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPost("detach")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Detach([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.DetachChildTenantAsync(session, childTenantId);
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

    // PUT: {tenantId}/v1/tenants/clear?childTenantId=abc
    /// <summary>
    ///     Clears the content of a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPut("clear")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Clear([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.ClearChildTenantAsync(session, childTenantId);
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

    // PUT: {tenantId}/v1/tenants/clearCache?childTenantId=abc
    /// <summary>
    ///     Clears the caches of a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpPut("clearCache")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearCache([Required] string childTenantId)
    {
        try
        {
            var correlationId = Guid.NewGuid();
            await _distributionEventHubService.PublishAsync(
                new PreUpdateTenant(childTenantId, correlationId, DateTime.Now));
            await Task.Delay(2000);
            await _distributionEventHubService.PublishAsync(
                new PosUpdateTenant(childTenantId, correlationId, DateTime.Now));

            return Ok("Cache cleared");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // DELETE: {tenantId}/v1/tenants?childTenantId=abc
    /// <summary>
    ///     Deletes a child tenant
    /// </summary>
    /// <param name="childTenantId">ID of the child tenant</param>
    [HttpDelete]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Delete([Required] string childTenantId)
    {
        try
        {
            var tenantContext = await GetTenantContextAsync();
            if (tenantContext == null)
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            using var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.DropChildTenantAsync(session, childTenantId);
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

    private static TenantDto CreateTenantDto(OctoTenant octoTenant)
    {
        return new TenantDto
        {
            TenantId = octoTenant.TenantId,
            Database = octoTenant.DatabaseName
        };
    }
}
