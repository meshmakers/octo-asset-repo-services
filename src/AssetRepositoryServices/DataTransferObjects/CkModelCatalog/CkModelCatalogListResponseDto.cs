namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for listing CK models from catalogs with pagination
/// </summary>
public class CkModelCatalogListResponseDto
{
    /// <summary>
    ///     List of CK model items
    /// </summary>
    public List<CkModelCatalogItemDto> Items { get; set; } = [];

    /// <summary>
    ///     Total count of models matching the query
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    ///     Number of items skipped
    /// </summary>
    public int Skip { get; set; }

    /// <summary>
    ///     Number of items taken
    /// </summary>
    public int Take { get; set; }
}
