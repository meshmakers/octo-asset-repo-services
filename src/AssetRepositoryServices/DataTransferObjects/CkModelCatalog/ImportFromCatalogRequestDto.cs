using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Request to import a CK model from a catalog into a tenant
/// </summary>
public class ImportFromCatalogRequestDto
{
    /// <summary>
    ///     Name of the catalog to import from (e.g., "PublicGitHub", "LocalFileSystem")
    /// </summary>
    [Required]
    public string CatalogName { get; set; } = string.Empty;

    /// <summary>
    ///     Full model ID including version (e.g., "Energy-2.0.0")
    /// </summary>
    [Required]
    public string ModelId { get; set; } = string.Empty;
}
