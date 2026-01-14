namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Represents a blueprint backup
/// </summary>
public class BlueprintBackupDto
{
    /// <summary>
    ///     Unique backup ID
    /// </summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>
    ///     Timestamp when the backup was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     Blueprint ID at the time of backup
    /// </summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>
    ///     Reason for the backup
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    ///     Size of the backup in bytes
    /// </summary>
    public long? SizeBytes { get; set; }
}
