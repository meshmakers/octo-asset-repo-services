using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Request to import multiple CK models from a catalog in dependency order
/// </summary>
public class ImportFromCatalogBatchRequestDto
{
    /// <summary>
    ///     Name of the catalog to import from
    /// </summary>
    [Required]
    public string CatalogName { get; set; } = string.Empty;

    /// <summary>
    ///     Model IDs to import, in dependency order (dependencies first)
    /// </summary>
    [Required]
    public List<string> ModelIds { get; set; } = [];
}
