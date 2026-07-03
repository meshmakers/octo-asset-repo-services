namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of refreshing a single blueprint catalog cache
/// </summary>
public class BlueprintCatalogRefreshResultDto
{
    /// <summary>
    ///     Name of the catalog
    /// </summary>
    public string CatalogName { get; set; } = string.Empty;

    /// <summary>
    ///     Outcome of the refresh: Refreshed, Skipped or Failed
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     Optional detail message (failure or skip reason)
    /// </summary>
    public string? Message { get; set; }
}
