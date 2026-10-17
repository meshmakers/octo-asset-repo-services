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

    /// <summary>
    ///     Tenant upgrade guard override (AB#6870). When <c>false</c> (default) an import that would break an installed
    ///     model is refused with HTTP 409 <c>CK_UPGRADE_BLOCKED</c>; nothing is imported. <c>true</c> imports anyway and
    ///     logs the blockers and the caller at WARN. Ignored by the read-only endpoints (ResolveDependencies,
    ///     CheckUpgrade). Requests of clients that predate the member behave as <c>false</c>.
    /// </summary>
    public bool ForceBreakingUpdate { get; set; }
}
