namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for the migration history endpoint.
/// </summary>
public class MigrationHistoryResponseDto
{
    /// <summary>
    ///     Migration history entries, sorted by ExecutedAt descending
    /// </summary>
    public List<MigrationHistoryEntryDto> Items { get; set; } = [];

    /// <summary>
    ///     Total number of migration entries
    /// </summary>
    public int TotalCount { get; set; }
}

/// <summary>
///     A single migration history entry for a CK model
/// </summary>
public class MigrationHistoryEntryDto
{
    /// <summary>
    ///     Name of the CK model that was migrated
    /// </summary>
    public required string CkModelName { get; set; }

    /// <summary>
    ///     Source version before migration
    /// </summary>
    public required string FromVersion { get; set; }

    /// <summary>
    ///     Target version after migration
    /// </summary>
    public required string ToVersion { get; set; }

    /// <summary>
    ///     When the migration was executed (UTC)
    /// </summary>
    public DateTime ExecutedAt { get; set; }

    /// <summary>
    ///     Whether the migration was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Number of entities affected
    /// </summary>
    public int EntitiesAffected { get; set; }

    /// <summary>
    ///     Number of entities added
    /// </summary>
    public int EntitiesAdded { get; set; }

    /// <summary>
    ///     Number of entities updated
    /// </summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>
    ///     Number of entities deleted
    /// </summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>
    ///     Duration of the migration in milliseconds
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    ///     Error messages if migration failed
    /// </summary>
    public List<string>? Errors { get; set; }

    /// <summary>
    ///     Warning messages generated during migration
    /// </summary>
    public List<string>? Warnings { get; set; }

    /// <summary>
    ///     Identifier of the backup created before migration
    /// </summary>
    public string? BackupId { get; set; }
}
