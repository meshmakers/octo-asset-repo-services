namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Represents a blueprint catalog
/// </summary>
public class BlueprintCatalogDto
{
    /// <summary>
    ///     Name of the catalog
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Description of the catalog
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
