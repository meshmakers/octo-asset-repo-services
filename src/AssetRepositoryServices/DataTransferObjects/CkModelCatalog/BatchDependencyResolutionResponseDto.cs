namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for batch dependency resolution.
///     Contains a flattened import list and per-model dependency trees.
/// </summary>
public class BatchDependencyResolutionResponseDto
{
    /// <summary>
    ///     Flattened, deduplicated, topologically sorted list of model IDs to import.
    ///     Dependencies come before dependents (ready for sequential import).
    /// </summary>
    public List<string> ModelsToImport { get; set; } = [];

    /// <summary>
    ///     Per-model dependency trees for UI display
    /// </summary>
    public List<DependencyResolutionResponseDto> DependencyTrees { get; set; } = [];
}
