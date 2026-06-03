namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for the library status endpoint.
///     Contains merged view of installed models + catalog availability.
/// </summary>
public class CkModelLibraryStatusResponseDto
{
    /// <summary>
    ///     All known models (installed + catalog-only)
    /// </summary>
    public List<CkModelLibraryStatusItemDto> Items { get; set; } = [];

    /// <summary>
    ///     Count of models that need action (ResolveFailed or update available)
    /// </summary>
    public int ModelsNeedingActionCount { get; set; }

    /// <summary>
    ///     Count of models whose compatibility check could not resolve one or more pinned
    ///     dependencies in any registered catalog (catalog publishing inconsistency).
    /// </summary>
    public int ModelsWithCatalogInconsistencyCount { get; set; }
}
