using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.ApiErrors;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for CK and RT model management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
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

    // POST: system/Models/ExportRtByQuery
    /// <summary>
    ///     Exports a runtime model by query
    /// </summary>
    /// <param name="tenantId">ID of tenant the request relies on to</param>
    /// <param name="exportModelRequestByQueryDto">The query options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByQuery")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportRtByQueryAsync([Required] string tenantId,
        [FromBody] ExportModelRequestByQueryDto exportModelRequestByQueryDto)
    {
        try
        {
            var args = new ExportRtByQueryCommandRequest(tenantId, exportModelRequestByQueryDto.QueryId);
            var r =
                await _exportRtByQueryCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new ExportModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerError(e.Message));
        }
    }
    
    // POST: system/Models/ExportRtByDeepGraph
    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">ID of tenant the request relies on to</param>
    /// <param name="exportModelRequestByDeepGraphDto">The deep graph options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByDeepGraph")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportRtByDeepGraphAsync([Required] string tenantId,
        [FromBody] ExportModelRequestByDeepGraphDto exportModelRequestByDeepGraphDto)
    {
        try
        {
            var args = new ExportRtByDeepGraphCommandRequest(tenantId,
                exportModelRequestByDeepGraphDto.OriginRtIds, 
                exportModelRequestByDeepGraphDto.OriginCkTypeId);
            var r =
                await _exportRtByDeepGraphCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new ExportModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerError(e.Message));
        }
    }

    // POST: system/Models/ImportRt
    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="tenantId">ID of tenant the request relies on to</param>
    /// <param name="file">The file with the RT model definition</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("ImportRt")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportRt([Required] string tenantId, IFormFile file)
    {
        try
        {
            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportRtCommandRequest(tenantId, cacheKey);
            var r =
                await _importRtCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new ExportModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerError(e.Message));
        }
    }

    // POST: system/Models/ImportCk
    /// <summary>
    ///     Imports a construction kit model
    /// </summary>
    /// <param name="tenantId">ID of tenant the request relies on to</param>
    /// <param name="file">The file with the CK model definition</param>
    /// <returns></returns>
    [HttpPost]
    //[Consumes("application/zip", "application/zip")]
    [Route("ImportCk")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportCk([Required] string tenantId, IFormFile file)
    {
        try
        {
            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportCkCommandRequest(tenantId, cacheKey);
            var r =
                await _importCkCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(r.JobId);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerError(e.Message));
        }
    }

    private async Task<string> AddFileToCache(string tenantId, IFormFile file)
    {
        await using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        var key = await _distributedCache.CreateStreamAsync(tenantId, memoryStream, file.ContentType, file.FileName, TimeSpan.FromHours(1));
        return key;
    }
}