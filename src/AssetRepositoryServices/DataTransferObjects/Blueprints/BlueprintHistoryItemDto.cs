namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Represents a single entry in the blueprint history of a tenant
/// </summary>
public class BlueprintHistoryItemDto
{
    /// <summary>
    ///     Blueprint ID that was applied
    /// </summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>
    ///     Timestamp when the blueprint was applied
    /// </summary>
    public DateTime AppliedAt { get; set; }

    /// <summary>
    ///     Application mode (Initial, Update, Migration)
    /// </summary>
    public string ApplicationMode { get; set; } = string.Empty;

    /// <summary>
    ///     Previous blueprint version (for updates)
    /// </summary>
    public string? PreviousVersion { get; set; }

    /// <summary>
    ///     Number of entities created
    /// </summary>
    public int EntitiesCreated { get; set; }

    /// <summary>
    ///     Number of entities updated
    /// </summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>
    ///     Number of entities deleted
    /// </summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>
    ///     Checksum of seed data
    /// </summary>
    public string? SeedDataChecksum { get; set; }
}
