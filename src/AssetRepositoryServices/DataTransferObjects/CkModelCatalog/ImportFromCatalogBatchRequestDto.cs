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

    /// <summary>
    ///     Tenant upgrade guard override (AB#6870). When <c>false</c> (default) an import that would break an installed
    ///     model is refused with HTTP 409 <c>CK_UPGRADE_BLOCKED</c>; nothing is imported. <c>true</c> imports anyway and
    ///     logs the blockers and the caller at WARN. The whole set is judged together and refused as a whole (no partial import). Requests of clients that predate the member behave as <c>false</c>.
    /// </summary>
    public bool ForceBreakingUpdate { get; set; }
}
