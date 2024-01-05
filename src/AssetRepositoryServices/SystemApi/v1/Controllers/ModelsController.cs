using System.ComponentModel.DataAnnotations;
using IdentityModel;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Common.ApiErrors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
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
    private readonly ICommandClient<ExportRtCommandRequest> _exportRtCommandClient;
    private readonly ICommandClient<ImportCkCommandRequest> _importCkCommandClient;
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache">Instance of distributed cache</param>
    /// <param name="exportRtCommandClient"></param>
    /// <param name="importRtCommandClient"></param>
    /// <param name="importCkCommandClient"></param>
    public ModelsController(IDistributedCacheService distributedCache,
        ICommandClient<ExportRtCommandRequest> exportRtCommandClient,
        ICommandClient<ImportRtCommandRequest> importRtCommandClient,
        ICommandClient<ImportCkCommandRequest> importCkCommandClient)
    {
        _distributedCache = distributedCache;
        _exportRtCommandClient = exportRtCommandClient;
        _importRtCommandClient = importRtCommandClient;
        _importCkCommandClient = importCkCommandClient;
    }

    // POST: system/Models/ExportRt
    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">Id of tenant the request relies to</param>
    /// <param name="exportModelRequestDto">The query, whose result data should be exported</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRt")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadOnlyPolicy)]
    public async Task<IActionResult> ExportRt([Required] string tenantId,
        [FromBody] ExportModelRequestDto exportModelRequestDto)
    {
        try
        {
            var args = new ExportRtCommandRequest(tenantId, exportModelRequestDto.QueryId);
            var r =
                await _exportRtCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new ExportModelResponseDto { JobId = r.JobId });
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
    /// <param name="tenantId">Id of tenant the request relies to</param>
    /// <param name="file">The file with the RT model definition</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("ImportRt")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> ImportRt([Required] string tenantId, [FromForm] IFormFile file)
    {
        try
        {
            var cacheKey = await AddFileToCache(file);
            var args = new ImportRtCommandRequest(tenantId, cacheKey);
            var r =
                await _importRtCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(r.JobId);
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
    /// <param name="tenantId">Id of tenant the request relies to</param>
    /// <param name="scopeId">The scope id of the model to import</param>
    /// <param name="file">The file with the CK model definition</param>
    /// <returns></returns>
    [HttpPost]
    //[Consumes("application/zip", "application/zip")]
    [Route("ImportCk")]
    [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> ImportCk([Required] string tenantId, ScopeIdsDto scopeId,
        [FromForm] IFormFile file)
    {
        try
        {
            var cacheKey = await AddFileToCache(file);
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

    private async Task<string> AddFileToCache(IFormFile file)
    {
        await using (var memoryStream = new MemoryStream())
        {
            await file.CopyToAsync(memoryStream);
            var key = Guid.NewGuid().ToString();
            await _distributedCache.CacheStreamAsync(key, memoryStream, file.ContentType, file.FileName, TimeSpan.FromHours(1));
            return key;
        }
    }
}