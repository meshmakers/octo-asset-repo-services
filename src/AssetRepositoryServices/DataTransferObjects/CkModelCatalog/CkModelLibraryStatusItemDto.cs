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
    ///     Whether the model needs action (ResolveFailed or HasUpdate)
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
}
