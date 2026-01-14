namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Information about available blueprint updates for a tenant
/// </summary>
public class BlueprintUpdateInfoDto
{
    /// <summary>
    ///     Current blueprint ID of the tenant
    /// </summary>
    public string? CurrentBlueprintId { get; set; }

    /// <summary>
    ///     Current blueprint version
    /// </summary>
    public string? CurrentVersion { get; set; }

    /// <summary>
    ///     Recommended version to update to
    /// </summary>
    public string? RecommendedVersion { get; set; }

    /// <summary>
    ///     Whether an update is available
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    ///     List of available versions
    /// </summary>
    public List<string> AvailableVersions { get; set; } = [];
}
