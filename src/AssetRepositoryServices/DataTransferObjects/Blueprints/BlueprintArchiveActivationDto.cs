namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     One archive of a blueprint seed and what the install or update did with it (AB#7991): archives the seed newly
///     created are activated (raw and time-range first, rollups in source order) and new rollups get a backfill job;
///     archives that already existed are listed but never touched.
/// </summary>
public class BlueprintArchiveActivationDto
{
    /// <summary>Runtime id of the archive.</summary>
    public string ArchiveRtId { get; set; } = string.Empty;

    /// <summary>Well-known name of the archive, if it has one.</summary>
    public string? WellKnownName { get; set; }

    /// <summary>CK type of the archive entity.</summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>The blueprint whose seed delivered the archive.</summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>True when the archive is a rollup (known for archives the apply tried to activate).</summary>
    public bool IsRollup { get; set; }

    /// <summary>
    ///     <c>Activated</c>, <c>SkippedPreExisting</c>, <c>SkippedOptOut</c>, <c>SkippedStreamDataDisabled</c> or
    ///     <c>Failed</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why the archive was skipped or failed; <c>null</c> when it was activated.</summary>
    public string? Reason { get; set; }

    /// <summary><c>NotApplicable</c>, <c>Queued</c>, <c>NothingToBackfill</c>, <c>NotQueued</c> or <c>Failed</c>.</summary>
    public string Backfill { get; set; } = string.Empty;

    /// <summary>The queued backfill job (follow it with the recompute jobs of the rollup); <c>null</c> otherwise.</summary>
    public string? BackfillJobRtId { get; set; }

    /// <summary>Why no backfill was queued or why queueing failed (for example the AB#7984 guard).</summary>
    public string? BackfillReason { get; set; }
}
