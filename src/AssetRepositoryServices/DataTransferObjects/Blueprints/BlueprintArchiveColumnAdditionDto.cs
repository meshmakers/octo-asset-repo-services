namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     A column a blueprint update added to an activated archive (AB#7997): an ingested column through
///     <c>ALTER TABLE … ADD COLUMN</c>, a computed column with its backfill queued as a background job.
/// </summary>
public class BlueprintArchiveColumnAdditionDto
{
    /// <summary>Runtime id of the archive.</summary>
    public string ArchiveRtId { get; set; } = string.Empty;

    /// <summary>Well-known name of the archive, if it has one.</summary>
    public string? ArchiveWellKnownName { get; set; }

    /// <summary>Attribute path of an ingested column, name of a computed column.</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>True for a computed column.</summary>
    public bool IsComputed { get; set; }

    /// <summary>The queued backfill job of a computed column (follow it with the recompute jobs of the archive).</summary>
    public string? BackfillJobRtId { get; set; }

    /// <summary>Why the column could not be added; <c>null</c> when it was added. A failed column is retried by applying the update again.</summary>
    public string? Error { get; set; }
}
