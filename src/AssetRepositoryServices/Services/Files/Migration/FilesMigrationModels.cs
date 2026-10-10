namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Legacy (System.Reporting) file data still present in a tenant — the cheap check of the sweep.
/// </summary>
public sealed record ReportingFilesCounts
{
    /// <summary>
    ///     Documents of type <c>System.Reporting/FileSystemItem</c> in the legacy collection.
    /// </summary>
    public long FileSystemItems { get; init; }

    /// <summary>
    ///     Documents of type <c>System.Reporting/Folder</c> in the legacy collection.
    /// </summary>
    public long Folders { get; init; }

    /// <summary>
    ///     Documents of type <c>System.Reporting/FolderRoot</c> in the legacy collection.
    /// </summary>
    public long FolderRoots { get; init; }

    /// <summary>
    ///     Associations whose <c>originCkTypeId</c> is a legacy type.
    /// </summary>
    public long AssociationOrigins { get; init; }

    /// <summary>
    ///     Associations whose <c>targetCkTypeId</c> is a legacy type.
    /// </summary>
    public long AssociationTargets { get; init; }

    /// <summary>
    ///     GridFS files whose owner stamp (<c>metadata.rtEntityId</c>) starts with a legacy type id.
    /// </summary>
    public long FileStamps { get; init; }

    /// <summary>
    ///     Legacy entity documents of all three types.
    /// </summary>
    public long Entities => FileSystemItems + Folders + FolderRoots;

    /// <summary>
    ///     True when nothing is left to move (steady state).
    /// </summary>
    public bool IsZero => Entities == 0 && AssociationOrigins == 0 && AssociationTargets == 0 && FileStamps == 0;

    /// <inheritdoc />
    public override string ToString()
    {
        return $"items={FileSystemItems}, folders={Folders}, roots={FolderRoots}, " +
               $"associationOrigins={AssociationOrigins}, associationTargets={AssociationTargets}, stamps={FileStamps}";
    }
}

/// <summary>
///     Outcome of one sweep run.
/// </summary>
public enum ReportingFilesSweepOutcome
{
    /// <summary>
    ///     The cheap check found no legacy data; nothing was written.
    /// </summary>
    NothingToDo,

    /// <summary>
    ///     Legacy data was moved and verified.
    /// </summary>
    Moved,

    /// <summary>
    ///     System.Files is not imported (collection or indexes missing); nothing was written.
    /// </summary>
    TargetModelMissing,

    /// <summary>
    ///     A moved batch could not be verified in the target collection; the source collection is kept.
    /// </summary>
    CountMismatch,

    /// <summary>
    ///     The sweep threw; see <see cref="ReportingFilesSweepResult.Errors" />.
    /// </summary>
    Failed,

    /// <summary>
    ///     The tenant does not exist (any more).
    /// </summary>
    TenantNotFound,

    /// <summary>
    ///     The sweep is switched off (<see cref="FilesMigrationOptions.SweepEnabled" />).
    /// </summary>
    Disabled,

    /// <summary>
    ///     Another pod holds the tenant's sweep lease; this run skipped.
    /// </summary>
    LeaseHeld,

    /// <summary>
    ///     A legacy folder root has the well-known name of an existing System.Files root (e.g. "Files"); the
    ///     move is aborted for the tenant instead of creating a duplicate root.
    /// </summary>
    RootConflict
}

/// <summary>
///     Result of one sweep run (also the content of its audit record).
/// </summary>
public sealed class ReportingFilesSweepResult
{
    /// <summary>
    ///     Tenant the sweep ran for.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    ///     Outcome.
    /// </summary>
    public ReportingFilesSweepOutcome Outcome { get; set; }

    /// <summary>
    ///     Legacy data before the sweep (null when the check could not run).
    /// </summary>
    public ReportingFilesCounts? Before { get; set; }

    /// <summary>
    ///     Legacy data after the sweep (null when nothing was written).
    /// </summary>
    public ReportingFilesCounts? After { get; set; }

    /// <summary>
    ///     Moved entities per legacy type id.
    /// </summary>
    public Dictionary<string, long> EntitiesMoved { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Rewritten <c>originCkTypeId</c> fields.
    /// </summary>
    public long AssociationOriginsUpdated { get; set; }

    /// <summary>
    ///     Rewritten <c>targetCkTypeId</c> fields.
    /// </summary>
    public long AssociationTargetsUpdated { get; set; }

    /// <summary>
    ///     Rewritten GridFS owner stamps.
    /// </summary>
    public long StampsUpdated { get; set; }

    /// <summary>
    ///     Legacy documents whose rtId already existed in System.Files ("type@rtId"): the System.Files version
    ///     was kept, the legacy document parked in <see cref="ReportingFilesMigrationConstants.ConflictCollectionName" />.
    /// </summary>
    public List<string> Conflicts { get; } = [];

    /// <summary>
    ///     Legacy folder roots whose well-known name collides with an existing System.Files root.
    /// </summary>
    public List<string> RootConflicts { get; } = [];

    /// <summary>
    ///     Errors (count mismatches, exceptions).
    /// </summary>
    public List<string> Errors { get; } = [];

    /// <summary>
    ///     Duration of the run.
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    ///     Total moved entities.
    /// </summary>
    public long TotalEntitiesMoved => EntitiesMoved.Values.Sum();

    /// <summary>
    ///     True when the sweep found legacy data (moved or not) — the tenant stays on the straggler timer.
    /// </summary>
    public bool FoundLegacyData => Before is { IsZero: false };
}

/// <summary>
///     An entity outside the file collections whose stored document names a System.Reporting file type
///     literally (pipeline definitions, data policies, queries, UI elements, …). The sweep does not
///     rewrite these; they are listed for the blueprint release (S9) and the runbook (S11).
/// </summary>
public sealed class LegacyTypeReferenceDto
{
    /// <summary>
    ///     Collection the entity lives in.
    /// </summary>
    public required string CollectionName { get; init; }

    /// <summary>
    ///     Runtime id.
    /// </summary>
    public required string RtId { get; init; }

    /// <summary>
    ///     Stored type id.
    /// </summary>
    public string? CkTypeId { get; init; }

    /// <summary>
    ///     Well-known name, if any.
    /// </summary>
    public string? RtWellKnownName { get; init; }

    /// <summary>
    ///     Blueprint that created the entity (<c>attributes.rtBlueprintSource</c>); null = tenant-local.
    /// </summary>
    public string? RtBlueprintSource { get; init; }

    /// <summary>
    ///     The legacy type ids found (e.g. <c>System.Reporting/FileSystemItem</c>).
    /// </summary>
    public required IReadOnlyList<string> Literals { get; init; }

    /// <summary>
    ///     Document paths of the string values that contain them (e.g. <c>attributes.pipelineDefinition</c>).
    /// </summary>
    public required IReadOnlyList<string> FieldPaths { get; init; }
}

/// <summary>
///     One audit record of the sweep.
/// </summary>
public sealed class FilesMigrationAuditDto
{
    /// <summary>
    ///     When the sweep ran (UTC).
    /// </summary>
    public DateTime ExecutedAt { get; init; }

    /// <summary>
    ///     What triggered it (TenantStart, StragglerTimer, …).
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>
    ///     Host (pod) that ran it.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    ///     Outcome.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>
    ///     Total moved entities.
    /// </summary>
    public long EntitiesMoved { get; init; }

    /// <summary>
    ///     Rewritten association type fields (origin + target).
    /// </summary>
    public long AssociationFieldsUpdated { get; init; }

    /// <summary>
    ///     Rewritten GridFS owner stamps.
    /// </summary>
    public long StampsUpdated { get; init; }

    /// <summary>
    ///     Legacy documents parked because their rtId already existed in System.Files.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    ///     Errors of the run.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
///     Pre-check report of the file data migration for one tenant (R4, design §6.2).
/// </summary>
public sealed class FilesMigrationStatusDto
{
    /// <summary>
    ///     Tenant.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    ///     When the report was built (UTC).
    /// </summary>
    public DateTime CheckedAt { get; init; }

    /// <summary>
    ///     Legacy data still present (the sweep's cheap check).
    /// </summary>
    public required ReportingFilesCounts Legacy { get; init; }

    /// <summary>
    ///     Documents in the legacy collection of other (derived or unknown) types, per type id. They are not
    ///     moved; the legacy collection is never dropped by the sweep.
    /// </summary>
    public required IReadOnlyDictionary<string, long> OtherLegacyTypes { get; init; }

    /// <summary>
    ///     Legacy folder roots whose well-known name collides with an existing System.Files root; the sweep
    ///     refuses to move the tenant while any exist.
    /// </summary>
    public required IReadOnlyList<string> RootConflicts { get; init; }

    /// <summary>
    ///     False when the literal scan hit <see cref="FilesMigrationOptions.ScanTimeout" /> and the reference
    ///     list is incomplete.
    /// </summary>
    public bool LiteralScanComplete { get; init; } = true;

    /// <summary>
    ///     True when System.Files is imported (target collection with indexes exists).
    /// </summary>
    public bool TargetModelReady { get; init; }

    /// <summary>
    ///     System.Files entities per type id.
    /// </summary>
    public required IReadOnlyDictionary<string, long> Target { get; init; }

    /// <summary>
    ///     Legacy files and folders without a parent (ParentChild) association; they move as they are.
    /// </summary>
    public long OrphanCount { get; init; }

    /// <summary>
    ///     Up to <see cref="FilesMigrationOptions.MaxReportedOrphans" /> orphan ids ("type@rtId").
    /// </summary>
    public required IReadOnlyList<string> Orphans { get; init; }

    /// <summary>
    ///     Number of entities outside the file collections that name a legacy type literally.
    /// </summary>
    public long LiteralReferenceCount { get; init; }

    /// <summary>
    ///     Up to <see cref="FilesMigrationOptions.MaxReportedReferences" /> of them.
    /// </summary>
    public required IReadOnlyList<LegacyTypeReferenceDto> LiteralReferences { get; init; }

    /// <summary>
    ///     RtEntity collections scanned for literal references.
    /// </summary>
    public int ScannedCollections { get; init; }

    /// <summary>
    ///     Documents scanned for literal references.
    /// </summary>
    public long ScannedDocuments { get; init; }

    /// <summary>
    ///     True when this instance keeps the tenant on the straggler timer.
    /// </summary>
    public bool StragglerSweepPending { get; init; }

    /// <summary>
    ///     The latest audit records of the sweep, newest first.
    /// </summary>
    public required IReadOnlyList<FilesMigrationAuditDto> RecentSweeps { get; init; }
}
