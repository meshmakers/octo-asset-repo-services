namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Names used by the move of the System.Reporting file data to System.Files (AB#6171 S3, AB#6175).
///     Everything is addressed by its stored string form, never through the CK cache: once
///     System.Reporting 3.0.0 has dropped the file types, the old type ids are unknown to the cache.
/// </summary>
public static class ReportingFilesMigrationConstants
{
    /// <summary>
    ///     Model name of the legacy file system types.
    /// </summary>
    public const string LegacyModelName = "System.Reporting";

    /// <summary>
    ///     Legacy type id of a file.
    /// </summary>
    public const string LegacyFileSystemItemCkTypeId = "System.Reporting/FileSystemItem";

    /// <summary>
    ///     Legacy type id of a folder.
    /// </summary>
    public const string LegacyFolderCkTypeId = "System.Reporting/Folder";

    /// <summary>
    ///     Legacy type id of a folder root.
    /// </summary>
    public const string LegacyFolderRootCkTypeId = "System.Reporting/FolderRoot";

    /// <summary>
    ///     Collection all legacy file entities share (collection root System.Reporting/FileSystemEntity).
    /// </summary>
    public const string LegacyEntityCollectionName = "RtEntity_SystemReportingFileSystemEntity";

    /// <summary>
    ///     Collection of all associations of a tenant.
    /// </summary>
    public const string AssociationCollectionName = "RtAssociation";

    /// <summary>
    ///     GridFS files collection (default bucket "fs").
    /// </summary>
    public const string GridFsFilesCollectionName = "fs.files";

    /// <summary>
    ///     Owner stamp of a GridFS file: <c>"&lt;ckTypeId&gt;@&lt;rtId&gt;"</c>.
    /// </summary>
    public const string GridFsOwnerStampField = "metadata.rtEntityId";

    /// <summary>
    ///     Dedicated audit collection of the sweep (one document per sweep that wrote or failed).
    ///     Not <c>System/MigrationHistory</c>: that collection drives the CK upgrade version detection.
    /// </summary>
    public const string AuditCollectionName = "FilesMigrationAudit";

    /// <summary>
    ///     Per-tenant lease collection of the sweep (one document per lease name, with expiry).
    /// </summary>
    public const string LeaseCollectionName = "FilesMigrationLease";

    /// <summary>
    ///     Quarantine of legacy documents whose rtId already exists in System.Files (a stale writer re-wrote
    ///     an already moved entity): the System.Files version is kept, the legacy document is parked here for
    ///     inspection and removed from the legacy collection.
    /// </summary>
    public const string ConflictCollectionName = "FilesMigrationConflicts";

    /// <summary>
    ///     CK model collection (document ids are "Name-x.y.z").
    /// </summary>
    public const string CkModelCollectionName = "CkModel";

    /// <summary>
    ///     Name of the sweep in log lines and audit records.
    /// </summary>
    public const string SweepName = "ReportingFilesMoveSweep";

    /// <summary>
    ///     Legacy type id → System.Files type id. Only the three concrete types exist as documents.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TypeMap = new Dictionary<string, string>
    {
        [LegacyFileSystemItemCkTypeId] = FileSystemConstants.FileSystemItemCkTypeId,
        [LegacyFolderCkTypeId] = FileSystemConstants.FolderCkTypeId,
        [LegacyFolderRootCkTypeId] = FileSystemConstants.FolderRootCkTypeId
    };

    /// <summary>
    ///     The legacy concrete type ids.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyTypeIds = TypeMap.Keys.ToList();

    /// <summary>
    ///     The System.Files concrete type ids.
    /// </summary>
    public static readonly IReadOnlyList<string> TargetTypeIds = TypeMap.Values.ToList();

    /// <summary>
    ///     Type names (without model) of System.Reporting whose literal use outside the file collections
    ///     is reported by the pre-check: the three concrete types plus the two abstract bases, which
    ///     queries and policies may name as well.
    /// </summary>
    public static readonly IReadOnlySet<string> ReportedLegacyTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "FileSystemItem", "Folder", "FolderRoot", "FileSystemEntity", "FileSystemContainer"
    };
}
