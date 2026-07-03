namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Response for a blueprint catalog cache refresh
/// </summary>
public class BlueprintCatalogRefreshResponseDto
{
    /// <summary>
    ///     Per-catalog refresh results
    /// </summary>
    public List<BlueprintCatalogRefreshResultDto> Results { get; set; } = [];
}
