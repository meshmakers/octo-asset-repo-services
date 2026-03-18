using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for CK and RT model management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class ModelsController : ControllerBase
{
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICommandClient<ExportRtByQueryCommandRequest> _exportRtByQueryCommandClient;
    private readonly ICommandClient<ExportRtByDeepGraphCommandRequest> _exportRtByDeepGraphCommandClient;
    private readonly ICommandClient<ImportCkCommandRequest> _importCkCommandClient;
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache">Instance of distributed cache</param>
    /// <param name="exportRtByQueryCommandClient"></param>
    /// <param name="exportRtByDeepGraphCommandClient"></param>
    /// <param name="importRtCommandClient"></param>
    /// <param name="importCkCommandClient"></param>
    public ModelsController(IDistributedCacheService distributedCache,
        ICommandClient<ExportRtByQueryCommandRequest> exportRtByQueryCommandClient,
        ICommandClient<ExportRtByDeepGraphCommandRequest> exportRtByDeepGraphCommandClient,
        ICommandClient<ImportRtCommandRequest> importRtCommandClient,
        ICommandClient<ImportCkCommandRequest> importCkCommandClient)
    {
        _distributedCache = distributedCache;
        _exportRtByQueryCommandClient = exportRtByQueryCommandClient;
        _exportRtByDeepGraphCommandClient = exportRtByDeepGraphCommandClient;
        _importRtCommandClient = importRtCommandClient;
        _importCkCommandClient = importCkCommandClient;
    }

    // POST: {tenantId}/v1/Models/ExportRtByQuery
    /// <summary>
    ///     Exports a runtime model by query
    /// </summary>
    /// <param name="exportModelRequestByQueryDto">The query options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByQuery")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportRtByQueryAsync(
        [FromBody] ExportModelRequestByQueryDto exportModelRequestByQueryDto)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var args = new ExportRtByQueryCommandRequest(tenantId, exportModelRequestByQueryDto.QueryId);
            var r =
                await _exportRtByQueryCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ExportRtByDeepGraph
    /// <summary>
    ///     Exports a runtime model by deep graph
    /// </summary>
    /// <param name="exportModelRequestByDeepGraphDto">The deep graph options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByDeepGraph")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportRtByDeepGraphAsync(
        [FromBody] ExportModelRequestByDeepGraphDto exportModelRequestByDeepGraphDto)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var args = new ExportRtByDeepGraphCommandRequest(tenantId,
                exportModelRequestByDeepGraphDto.OriginRtIds,
                exportModelRequestByDeepGraphDto.OriginCkTypeId);
            var r =
                await _exportRtByDeepGraphCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ImportRt
    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="importStrategy">The import strategy to use for the import</param>
    /// <param name="file">The file with the RT model definition</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("ImportRt")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportRt([Required] ImportStrategyDto importStrategy, [Required] IFormFile file)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var insertStrategy = importStrategy switch
            {
                ImportStrategyDto.InsertOnly => ImportStrategy.Insert,
                ImportStrategyDto.Upsert => ImportStrategy.Upsert,
                _ => throw new ArgumentOutOfRangeException(nameof(importStrategy), importStrategy, null)
            };
            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportRtCommandRequest(tenantId, insertStrategy, cacheKey);
            var r =
                await _importRtCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ImportCk
    /// <summary>
    ///     Imports a construction kit model
    /// </summary>
    /// <param name="file">The file with the CK model definition</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ImportCk")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportCk(IFormFile file)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportCkCommandRequest(tenantId, cacheKey);
            var r =
                await _importCkCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    private async Task<string> AddFileToCache(string tenantId, IFormFile file)
    {
        await using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        var key = await _distributedCache.CreateStreamAsync(tenantId, memoryStream, file.ContentType, file.FileName,
            TimeSpan.FromHours(1));
        return key;
    }
}
