namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Preview of changes that would be applied by a blueprint update
/// </summary>
public class BlueprintUpdatePreviewDto
{
    /// <summary>
    ///     Target version to update to
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Number of entities that would be added
    /// </summary>
    public int EntitiesToAdd { get; set; }

    /// <summary>
    ///     Number of entities that would be updated
    /// </summary>
    public int EntitiesToUpdate { get; set; }

    /// <summary>
    ///     Number of entities that would be deleted
    /// </summary>
    public int EntitiesToDelete { get; set; }

    /// <summary>
    ///     List of conflicts that would occur
    /// </summary>
    public List<BlueprintConflictDto> Conflicts { get; set; } = [];

    /// <summary>
    ///     List of warnings
    /// </summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
///     Represents a conflict during blueprint update
/// </summary>
public class BlueprintConflictDto
{
    /// <summary>
    ///     Entity ID that has a conflict
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    ///     Description of the conflict
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Suggested resolution
    /// </summary>
    public string? SuggestedResolution { get; set; }
}
