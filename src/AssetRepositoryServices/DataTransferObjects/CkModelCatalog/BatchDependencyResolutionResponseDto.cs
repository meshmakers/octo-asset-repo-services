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

    /// <summary>
    ///     AB#6870 (tenant upgrade guard): true when importing <see cref="ModelsToImport" /> as one update set would break an installed model. Judged read-only with
    ///     the same guard that refuses the import (HTTP 409 <c>CK_UPGRADE_BLOCKED</c>), so a preview agrees with the
    ///     refusal. <c>false</c> and empty for clients that ignore the members.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    ///     AB#6870: why the import is blocked - one entry per installed model and pin the update set would break
    ///     (empty when <see cref="IsBlocked" /> is <c>false</c>).
    /// </summary>
    public List<CkUpgradeBlockerDto> Blockers { get; set; } = [];
}
