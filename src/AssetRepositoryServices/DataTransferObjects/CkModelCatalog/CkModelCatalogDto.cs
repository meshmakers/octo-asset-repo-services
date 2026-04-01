namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Represents a CK model catalog source
/// </summary>
public class CkModelCatalogDto
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
