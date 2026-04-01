using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for CK model catalog management.
///     Provides system-scoped endpoints to browse, search, and manage
///     Construction Kit model catalogs.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class CkModelCatalogController : ControllerBase
{
    private readonly ICatalogService _catalogService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="catalogService">CK model catalog service</param>
    public CkModelCatalogController(ICatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    // GET system/v1/ckmodelcatalog
    /// <summary>
    ///     Lists all available CK models from all catalogs
    /// </summary>
    /// <param name="pagingParams">Optional paging parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated list of available CK models</returns>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkModelCatalogListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAll(
        [FromQuery] PagingParams? pagingParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var skip = pagingParams?.Skip ?? 0;
            var take = pagingParams?.Take ?? 20;

            var result = await _catalogService.ListAsync(skip, take, cancellationToken: cancellationToken);

            var response = MapToListResponse(result, skip, take);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/ckmodelcatalog/search?q={term}
    /// <summary>
    ///     Searches for CK models matching the search term across all catalogs
    /// </summary>
    /// <param name="q">Search term</param>
    /// <param name="pagingParams">Optional paging parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated list of matching CK models</returns>
    [HttpGet("search")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkModelCatalogListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Search(
        [Required] string q,
        [FromQuery] PagingParams? pagingParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest(new OperationFailedErrorDto("Search term is required"));
            }

            var skip = pagingParams?.Skip ?? 0;
            var take = pagingParams?.Take ?? 20;

            var result = await _catalogService.SearchAsync(q, skip, take, cancellationToken: cancellationToken);

            var response = MapToListResponse(result, skip, take);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/ckmodelcatalog/catalogs
    /// <summary>
    ///     Lists all available CK model catalog sources
    /// </summary>
    /// <returns>List of available catalogs with name and description</returns>
    [HttpGet("catalogs")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<CkModelCatalogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public IActionResult GetCatalogs()
    {
        try
        {
            var catalogs = _catalogService.GetCatalogList()
                .Select(t => new CkModelCatalogDto
                {
                    Name = t.Item1,
                    Description = t.Item2
                });

            return Ok(catalogs);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/ckmodelcatalog/{catalogName}
    /// <summary>
    ///     Lists CK models from a specific catalog
    /// </summary>
    /// <param name="catalogName">Name of the catalog to list models from</param>
    /// <param name="pagingParams">Optional paging parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated list of CK models from the specified catalog</returns>
    [HttpGet("{catalogName}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkModelCatalogListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetByCatalog(
        [Required] string catalogName,
        [FromQuery] PagingParams? pagingParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var skip = pagingParams?.Skip ?? 0;
            var take = pagingParams?.Take ?? 20;

            var result =
                await _catalogService.ListAsync(catalogName, skip, take, cancellationToken: cancellationToken);

            var response = MapToListResponse(result, skip, take);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // HEAD system/v1/ckmodelcatalog/{modelId}
    /// <summary>
    ///     Checks if a CK model exists in any catalog
    /// </summary>
    /// <param name="modelId">CK model ID (e.g., "System-1.0.0")</param>
    /// <returns>200 OK if the model exists, 404 Not Found otherwise</returns>
    [HttpHead("{modelId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Exists([Required] string modelId)
    {
        try
        {
            var ckModelId = new CkModelId(modelId);
            var exists = await _catalogService.IsExistingAsync(ckModelId);

            return exists ? Ok() : NotFound();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/ckmodelcatalog/{catalogName}/{modelId}
    /// <summary>
    ///     Gets details of a specific CK model from a catalog
    /// </summary>
    /// <param name="catalogName">Name of the catalog</param>
    /// <param name="modelId">CK model ID (e.g., "System-1.0.0")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>CK model details</returns>
    [HttpGet("{catalogName}/{modelId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkModelCatalogItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetModel(
        [Required] string catalogName,
        [Required] string modelId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ckModelId = new CkModelId(modelId);
            var operationResult = new OperationResult();
            var model = await _catalogService.GetAsync(catalogName, ckModelId, operationResult,
                cancellationToken: cancellationToken);

            if (model == null)
            {
                return NotFound();
            }

            var response = new CkModelCatalogItemDto
            {
                Id = model.ModelId.FullName,
                Name = model.ModelId.Name,
                Version = model.ModelId.Version.ToString(),
                Description = model.Description,
                CatalogName = catalogName
            };

            return Ok(response);
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST system/v1/ckmodelcatalog/refresh
    /// <summary>
    ///     Refreshes the cache of all CK model catalogs
    /// </summary>
    /// <returns>204 No Content on success</returns>
    [HttpPost("refresh")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RefreshAll()
    {
        try
        {
            await _catalogService.RefreshAllCatalogCachesAsync();
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST system/v1/ckmodelcatalog/{catalogName}/refresh
    /// <summary>
    ///     Refreshes the cache of a specific CK model catalog
    /// </summary>
    /// <param name="catalogName">Name of the catalog to refresh</param>
    /// <returns>204 No Content on success</returns>
    [HttpPost("{catalogName}/refresh")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RefreshCatalog([Required] string catalogName)
    {
        try
        {
            await _catalogService.RefreshCatalogCacheAsync(catalogName);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    private static CkModelCatalogListResponseDto MapToListResponse(ModelListResult result, int skip, int take)
    {
        return new CkModelCatalogListResponseDto
        {
            Items = result.ModelResultItems.Select(item => new CkModelCatalogItemDto
            {
                Id = item.ModelId.FullName,
                Name = item.ModelId.Name,
                Version = item.ModelId.Version.ToString(),
                Description = item.Description,
                CatalogName = item.CatalogName
            }).ToList(),
            TotalCount = result.TotalCount,
            Skip = skip,
            Take = take
        };
    }
}
