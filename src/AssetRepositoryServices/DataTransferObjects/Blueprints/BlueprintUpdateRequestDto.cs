namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Request to apply a blueprint update
/// </summary>
public class BlueprintUpdateRequestDto
{
    /// <summary>
    ///     Target blueprint version to update to
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Update mode: Safe, Merge, Full, Migration
    /// </summary>
    public string UpdateMode { get; set; } = "Merge";

    /// <summary>
    ///     Whether this is a dry run (preview only, no changes)
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    ///     Conflict resolutions for specific entities
    /// </summary>
    public Dictionary<string, string>? ConflictResolutions { get; set; }
}
