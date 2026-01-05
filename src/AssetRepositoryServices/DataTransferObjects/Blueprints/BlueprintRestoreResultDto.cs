namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a backup restore operation
/// </summary>
public class BlueprintRestoreResultDto
{
    /// <summary>
    ///     Whether the restore was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Number of entities restored
    /// </summary>
    public int EntitiesRestored { get; set; }

    /// <summary>
    ///     Messages from the restore operation
    /// </summary>
    public List<string> Messages { get; set; } = [];
}
