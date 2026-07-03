using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for blueprint catalog management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class BlueprintsController : ControllerBase
{
    private readonly IBlueprintCatalogManager _catalogManager;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="catalogManager">Blueprint catalog manager</param>
    public BlueprintsController(IBlueprintCatalogManager catalogManager)
    {
        _catalogManager = catalogManager;
    }

    // GET system/v1/blueprints
    /// <summary>
    ///     Lists all available blueprints from all catalogs
    /// </summary>
    /// <param name="pagingParams">Optional paging parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available blueprints</returns>
    [HttpGet]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAll(
        [FromQuery] PagingParams? pagingParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var skip = pagingParams?.Skip ?? 0;
            var take = pagingParams?.Take ?? 20;

            var result = await _catalogManager.ListAsync(skip, take, cancellationToken: cancellationToken);

            var response = new BlueprintListResponseDto
            {
                Items = result.Items.Select(item => new BlueprintDto
                {
                    Id = item.BlueprintId.FullName,
                    Name = item.BlueprintId.Name,
                    Version = item.BlueprintId.Version.ToString(),
                    Description = item.Description,
                    CatalogName = item.CatalogName
                }).ToList(),
                TotalCount = result.TotalCount,
                Skip = skip,
                Take = take
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/blueprints/search?q={term}
    /// <summary>
    ///     Searches for blueprints matching the search term
    /// </summary>
    /// <param name="q">Search term</param>
    /// <param name="pagingParams">Optional paging parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of matching blueprints</returns>
    [HttpGet("search")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintListResponseDto), StatusCodes.Status200OK)]
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

            var result = await _catalogManager.SearchAsync(q, skip, take, cancellationToken: cancellationToken);

            var response = new BlueprintListResponseDto
            {
                Items = result.Items.Select(item => new BlueprintDto
                {
                    Id = item.BlueprintId.FullName,
                    Name = item.BlueprintId.Name,
                    Version = item.BlueprintId.Version.ToString(),
                    Description = item.Description,
                    CatalogName = item.CatalogName
                }).ToList(),
                TotalCount = result.TotalCount,
                Skip = skip,
                Take = take
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET system/v1/blueprints/catalogs
    /// <summary>
    ///     Lists all available blueprint catalogs
    /// </summary>
    /// <returns>List of available catalogs</returns>
    [HttpGet("catalogs")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<BlueprintCatalogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public IActionResult GetCatalogs()
    {
        try
        {
            var catalogs = _catalogManager.GetCatalogList()
                .Select(t => new BlueprintCatalogDto
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

    // POST system/v1/blueprints/catalogs/refresh
    /// <summary>
    ///     Refreshes the caches of all blueprint catalogs (always forced)
    /// </summary>
    /// <returns>Per-catalog refresh results</returns>
    [HttpPost("catalogs/refresh")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(BlueprintCatalogRefreshResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RefreshAllCatalogs()
    {
        try
        {
            var results = await _catalogManager.RefreshAllCatalogCachesAsync(force: true);

            return Ok(MapToRefreshResponse(results));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST system/v1/blueprints/catalogs/{catalogName}/refresh
    /// <summary>
    ///     Refreshes the cache of a specific blueprint catalog (always forced)
    /// </summary>
    /// <param name="catalogName">Name of the catalog to refresh (case-insensitive)</param>
    /// <returns>The refresh result for the catalog</returns>
    [HttpPost("catalogs/{catalogName}/refresh")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(BlueprintCatalogRefreshResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RefreshCatalog([Required] string catalogName)
    {
        try
        {
            var result = await _catalogManager.RefreshCatalogCacheAsync(catalogName, force: true);

            return Ok(MapToRefreshResponse([result]));
        }
        catch (BlueprintCatalogException ex)
        {
            // The only BlueprintCatalogException the manager throws on this path is CatalogNotFound;
            // refresh failures of the catalog itself are reported inside the result.
            return NotFound(new NotFoundErrorDto(ex.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // HEAD system/v1/blueprints/{blueprintId}
    /// <summary>
    ///     Checks if a blueprint exists
    /// </summary>
    /// <param name="blueprintId">Blueprint ID (e.g., "MyBlueprint-1.0.0")</param>
    /// <returns>200 OK if exists, 404 Not Found otherwise</returns>
    [HttpHead("{blueprintId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Exists([Required] string blueprintId)
    {
        try
        {
            var id = new BlueprintId(blueprintId);
            var exists = await _catalogManager.IsExistingAsync(id);

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

    // GET system/v1/blueprints/{blueprintId}
    /// <summary>
    ///     Gets details of a specific blueprint
    /// </summary>
    /// <param name="blueprintId">Blueprint ID (e.g., "MyBlueprint-1.0.0")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Blueprint details</returns>
    [HttpGet("{blueprintId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BlueprintDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        [Required] string blueprintId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var id = new BlueprintId(blueprintId);
            var operationResult = new OperationResult();
            var blueprint = await _catalogManager.TryGetAsync(id, operationResult, cancellationToken: cancellationToken);

            if (blueprint == null)
            {
                return NotFound();
            }

            var response = new BlueprintDto
            {
                Id = blueprint.BlueprintId.FullName,
                Name = blueprint.BlueprintId.Name,
                Version = blueprint.BlueprintId.Version.ToString(),
                Description = blueprint.Description
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

    private static BlueprintCatalogRefreshResponseDto MapToRefreshResponse(
        IReadOnlyList<BlueprintCatalogRefreshResult> results)
    {
        return new BlueprintCatalogRefreshResponseDto
        {
            Results = results.Select(r => new BlueprintCatalogRefreshResultDto
            {
                CatalogName = r.CatalogName,
                Status = r.Status.ToString(),
                Message = r.Message
            }).ToList()
        };
    }
}
