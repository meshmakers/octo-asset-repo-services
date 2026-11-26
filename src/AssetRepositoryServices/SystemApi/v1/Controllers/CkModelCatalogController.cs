using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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

    // GET system/v1/ckmodelcatalog/{catalogName}/{modelId}/types
    /// <summary>
    ///     Reads the types (with attributes and associations), association roles and enums of a catalog CK
    ///     model version without importing it into a tenant. Read-only, paged and bounded (take is capped at
    ///     <see cref="CkCatalogModelTypesMapper.MaxTake" />). Same authorization as <see cref="GetModel" />.
    /// </summary>
    /// <param name="catalogName">Name of the catalog</param>
    /// <param name="modelId">CK model ID with version (e.g., "Basic-2.7.0")</param>
    /// <param name="filter">Optional text a type's id, description or attribute names must contain (case-insensitive)</param>
    /// <param name="skip">Types to skip</param>
    /// <param name="take">Page size, capped at 50 (default 20)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>One page of the model's types; problem+json 404 with code CkCatalogNotFound or CkCatalogModelNotFound; 400 InvalidCkModelId; 503 CkCatalogUnavailable when the catalog cannot be read</returns>
    [HttpGet("{catalogName}/{modelId}/types")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkCatalogModelTypesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetModelTypes(
        [Required] string catalogName,
        [Required] string modelId,
        [FromQuery] string? filter = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = CkCatalogModelTypesMapper.DefaultTake,
        CancellationToken cancellationToken = default)
    {
        // A full id with version is required: CkModelId("Basic") silently means Basic-1.0.0, which would read a model
        // the caller never asked for.
        CkModelId? ckModelId = null;
        try
        {
            if (modelId.IndexOf('-') > 0 && !modelId.EndsWith('-'))
            {
                ckModelId = new CkModelId(modelId);
                _ = ckModelId.Version.ToString();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
        {
            ckModelId = null;
        }

        if (ckModelId == null)
        {
            return CatalogProblem(StatusCodes.Status400BadRequest, "Invalid CK model id",
                $"'{modelId}' is not a CK model id with version (e.g. Basic-2.7.0).",
                CkCatalogModelTypesErrorCodes.InvalidCkModelId);
        }

        try
        {
            var catalog = _catalogService.GetCatalogList()
                .FirstOrDefault(c => string.Equals(c.Item1, catalogName, StringComparison.OrdinalIgnoreCase));
            if (catalog == null)
            {
                return CatalogProblem(StatusCodes.Status404NotFound, "CK model catalog not found",
                    $"CK model catalog '{catalogName}' not found.", CkCatalogModelTypesErrorCodes.CkCatalogNotFound);
            }

            CkCompiledModelRoot? model;
            try
            {
                model = await _catalogService.GetAsync(catalog.Item1, ckModelId, new OperationResult(),
                    cancellationToken: cancellationToken);
            }
            catch (ModelCatalogException ex) when (ex.IsModelNotFound)
            {
                model = null;
            }
            catch (ModelCatalogException ex)
            {
                // The catalog could not be read (unreachable, rate limit, timeout, broken index): that is no answer to
                // "does the model exist", so it must not look like "not found" to a caller that reuses public kits.
                return CatalogProblem(StatusCodes.Status503ServiceUnavailable, "CK model catalog unavailable",
                    $"CK model catalog '{catalog.Item1}' could not be read: {ex.Message}",
                    CkCatalogModelTypesErrorCodes.CkCatalogUnavailable);
            }

            if (model == null)
            {
                return CatalogProblem(StatusCodes.Status404NotFound, "CK model not found in catalog",
                    $"CK model '{ckModelId.FullName}' not found in catalog '{catalog.Item1}'.",
                    CkCatalogModelTypesErrorCodes.CkCatalogModelNotFound);
            }

            return Ok(CkCatalogModelTypesMapper.Map(catalog.Item1, model, filter, skip, take));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>An <c>application/problem+json</c> answer with the stable <c>code</c> extension.</summary>
    private static ObjectResult CatalogProblem(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Title = title, Status = status, Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    // POST system/v1/ckmodelcatalog/refresh
    /// <summary>
    ///     Refreshes the cache of all CK model catalogs
    /// </summary>
    /// <returns>204 No Content on success</returns>
    [HttpPost("refresh")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
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
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
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
