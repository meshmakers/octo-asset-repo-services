namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Represents a single item in a dependency resolution tree
/// </summary>
public class DependencyResolutionItemDto
{
    /// <summary>
    ///     Full model ID including version (e.g., "Energy-2.0.0")
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    ///     Model name without version (e.g., "Energy")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Required version from the catalog (e.g., "2.0.0")
    /// </summary>
    public string RequiredVersion { get; set; } = string.Empty;

    /// <summary>
    ///     CK v2: the version range the parent declares for this dependency when the parent is a range-retaining
    ///     model (e.g. "Basic-[2.0,3.0) (floor 2.0.0)"); null for an exact compile-time pin. Display only — the
    ///     action already reflects it. <see cref="RequiredVersion" /> stays the pinned (floor) version.
    /// </summary>
    public string? RequiredRange { get; set; }

    /// <summary>
    ///     Currently installed version in the tenant, or null if not installed
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    ///     Action needed: "install" (not present), "update" (different version), "none" (exact match)
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    ///     Transitive dependencies of this model
    /// </summary>
    public List<DependencyResolutionItemDto> Dependencies { get; set; } = [];
}
