namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for the CK model upgrade pre-flight check.
///     Indicates whether importing a specific model version will trigger migrations.
/// </summary>
public class UpgradeCheckResponseDto
{
    /// <summary>
    ///     Name of the CK model (e.g., "System")
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    ///     Currently installed version in the tenant, or null if not installed
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    ///     Target version from the catalog
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Whether an upgrade/migration is needed
    /// </summary>
    public bool UpgradeNeeded { get; set; }

    /// <summary>
    ///     Whether a migration path exists for the version transition
    /// </summary>
    public bool MigrationPathAvailable { get; set; }

    /// <summary>
    ///     Whether the migration of the model itself contains breaking changes. Does not cover installed dependents,
    ///     see <see cref="IsBlocked" />.
    /// </summary>
    public bool HasBreakingChanges { get; set; }

    /// <summary>
    ///     Error message if the upgrade check failed, null otherwise
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     AB#6868 (tenant upgrade guard, read-only): true when importing the target version would break an installed
    ///     model. Optional: older clients ignore it.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    ///     AB#6868: the installed models the target version would break; empty when not blocked.
    /// </summary>
    public List<CkUpgradeBlockerDto> Blockers { get; set; } = [];
}
