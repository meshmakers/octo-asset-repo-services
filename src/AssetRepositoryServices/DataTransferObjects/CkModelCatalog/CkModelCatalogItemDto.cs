namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Represents a CK model from a catalog
/// </summary>
public class CkModelCatalogItemDto
{
    /// <summary>
    ///     Full model ID including version (e.g., "System-1.0.0")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Model name without version (e.g., "System")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Model version (e.g., "1.0.0")
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     Optional description of the model
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Name of the catalog containing this model
    /// </summary>
    public string CatalogName { get; set; } = string.Empty;
}
