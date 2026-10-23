namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Represents the combined status of a CK model library:
///     installed state merged with catalog availability.
/// </summary>
public class CkModelLibraryStatusItemDto
{
    /// <summary>
    ///     Model name (e.g., "System", "Energy")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Currently installed version, or null if not installed
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    ///     Current model state: "Available", "ResolveFailed", "Importing", or null if not installed
    /// </summary>
    public string? ModelState { get; set; }

    /// <summary>
    ///     AB#6674: why the installed model is <c>ResolveFailed</c> - plain English text naming models, versions, ranges
    ///     and pins; <c>null</c> when the model is not failed or not installed. For display only, never parse it.
    /// </summary>
    public string? ResolveFailedReason { get; set; }

    /// <summary>
    ///     Dependencies of the installed model (fullName format)
    /// </summary>
    public List<string> Dependencies { get; set; } = [];

    /// <summary>
    ///     Latest available version in catalogs, or null if not in any catalog
    /// </summary>
    public string? CatalogVersion { get; set; }

    /// <summary>
    ///     Whether a newer version is available in catalogs (semantic comparison)
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    ///     Whether the model needs action (ResolveFailed or HasUpdate). <c>false</c> for a blocked row
    ///     (<see cref="IsBlocked" />), so clients that predate the guard do not offer the update.
    /// </summary>
    public bool NeedsAction { get; set; }

    /// <summary>
    ///     Name of the catalog containing the latest version
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>
    ///     Full model ID of the catalog version for import (e.g., "Energy-2.0.0")
    /// </summary>
    public string? FullModelId { get; set; }

    /// <summary>
    ///     Whether this model is managed by a service (System, System.Identity, etc.)
    ///     Service-managed models should not be updated by the user.
    /// </summary>
    public bool IsServiceManaged { get; set; }

    /// <summary>
    ///     Whether the catalog version is compatible with installed system model versions.
    ///     Checked transitively through the full dependency chain.
    /// </summary>
    public bool IsCompatible { get; set; } = true;

    /// <summary>
    ///     Reason for incompatibility, or null if compatible
    /// </summary>
    public string? IncompatibilityReason { get; set; }

    /// <summary>
    ///     Catalog model IDs that this model's compatibility check could not resolve in any
    ///     registered catalog. Non-empty implies a publishing inconsistency (a compiled model
    ///     declares a pinned dependency on a version that was never published, or was removed)
    ///     and the model cannot be installed as-is.
    /// </summary>
    public List<string> UnresolvedDependencies { get; set; } = [];

    /// <summary>
    ///     True when this row has unresolved dependencies. Convenience flag derived from
    ///     <see cref="UnresolvedDependencies"/> so the UI does not have to compute it.
    /// </summary>
    public bool HasCatalogInconsistency { get; set; }

    /// <summary>
    ///     AB#6868 (tenant upgrade guard, read-only): true when importing the offered catalog version
    ///     (<see cref="CatalogVersion" />) would break an installed model, for example because it is exact-pinned to the
    ///     version the update replaces. A blocked row keeps <see cref="HasUpdate" /> <c>true</c> and has
    ///     <see cref="NeedsAction" /> <c>false</c>. Never set for service-managed rows, rows without an update and
    ///     catalog-only rows. Optional: older clients ignore it.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    ///     AB#6868: why the row is blocked, one entry per installed model and pin the update would break; empty when
    ///     <see cref="IsBlocked" /> is <c>false</c>.
    /// </summary>
    public List<CkUpgradeBlockerDto> Blockers { get; set; } = [];

    /// <summary>
    ///     AB#6868: true when the installed model declares dependency ranges (range-retaining, CK language 2), so
    ///     operators can see which installed models are still exact-pinned without database access (F2.6, AB#6708).
    ///     <c>false</c> for exact-pinned classic models. For catalog-only rows it reports the range retention of the
    ///     catalog model (AB#7408).
    /// </summary>
    public bool IsRangeRetaining { get; set; }
}
