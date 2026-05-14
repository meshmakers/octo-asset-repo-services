namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a blueprint uninstall request.
/// </summary>
public class BlueprintUninstallResultDto
{
    /// <summary>True when the uninstall completed.</summary>
    public bool Success { get; set; }

    /// <summary>Fully-qualified id of the blueprint that was uninstalled, if any.</summary>
    public string? UninstalledBlueprintId { get; set; }

    /// <summary>Number of locked entities erased from the tenant.</summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>Blueprint ids that were cascade-uninstalled.</summary>
    public List<string> CascadedDependencies { get; set; } = [];

    /// <summary>
    ///     Other installed blueprints that still depend on the target. Populated
    ///     when uninstall is blocked because cascade was not requested.
    /// </summary>
    public List<string> BlockingDependents { get; set; } = [];

    /// <summary>Warnings reported during the operation.</summary>
    public List<string> Warnings { get; set; } = [];
}
