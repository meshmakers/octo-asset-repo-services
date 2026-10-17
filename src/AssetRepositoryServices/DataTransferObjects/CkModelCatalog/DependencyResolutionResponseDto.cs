namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for the dependency resolution endpoint.
///     Contains the full dependency tree with install/update actions.
/// </summary>
public class DependencyResolutionResponseDto
{
    /// <summary>
    ///     The root model with its resolved dependency tree
    /// </summary>
    public DependencyResolutionItemDto RootModel { get; set; } = null!;

    /// <summary>
    ///     AB#6870 (tenant upgrade guard): true when importing the models of this tree that need an install or update (the flattened import set) would break an installed model. Judged read-only with
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
