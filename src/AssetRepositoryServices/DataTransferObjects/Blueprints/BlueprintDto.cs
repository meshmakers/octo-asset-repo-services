namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Represents a blueprint from a catalog
/// </summary>
public class BlueprintDto
{
    /// <summary>
    ///     Full blueprint ID including version (e.g., "MyBlueprint-1.0.0")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Blueprint name without version
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Blueprint version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     Optional description of the blueprint
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Name of the catalog containing this blueprint
    /// </summary>
    public string CatalogName { get; set; } = string.Empty;
}
