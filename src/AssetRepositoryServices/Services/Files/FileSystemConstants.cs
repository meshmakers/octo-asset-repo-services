namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Names and identifiers of the platform file system (System.Files, AB#6171).
/// </summary>
public static class FileSystemConstants
{
    /// <summary>
    ///     Model name of the file system CK model.
    /// </summary>
    public const string ModelName = "System.Files";

    /// <summary>
    ///     Type id of a file.
    /// </summary>
    public const string FileSystemItemCkTypeId = "System.Files/FileSystemItem";

    /// <summary>
    ///     Type id of a folder.
    /// </summary>
    public const string FolderCkTypeId = "System.Files/Folder";

    /// <summary>
    ///     Type id of a folder root.
    /// </summary>
    public const string FolderRootCkTypeId = "System.Files/FolderRoot";

    /// <summary>
    ///     Abstract base type of all file system entries.
    /// </summary>
    public const string FileSystemEntityCkTypeId = "System.Files/FileSystemEntity";

    /// <summary>
    ///     Association role between a file or folder and its parent folder / root.
    /// </summary>
    public const string ParentChildRoleId = "System/ParentChild";

    /// <summary>
    ///     Name of the collection all System.Files entities share.
    /// </summary>
    public const string EntityCollectionName = "RtEntity_SystemFilesFileSystemEntity";

    /// <summary>
    ///     Display name of the default root seeded in every tenant (D8).
    /// </summary>
    public const string DefaultRootName = "Files";

    /// <summary>
    ///     Well-known name of the default root seeded in every tenant (D8).
    /// </summary>
    public const string DefaultRootWellKnownName = "Files";

    /// <summary>
    ///     Prefix of the folder roots the Reporting service owns.
    /// </summary>
    public const string ReportingRootPrefix = "ReportingAssets_";

    /// <summary>
    ///     Well-known names that cannot be used for a folder root, because the REST bytes API
    ///     (<c>/{tenant}/v1/files/{root}/…</c>) uses them as literal route segments.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedRootWellKnownNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "capabilities", "zip", "items", "stats" };

    /// <summary>
    ///     True when a root is owned by a service and must not be renamed or deleted: the default root
    ///     <c>Files</c> and the Reporting roots (<c>ReportingAssets_*</c>).
    /// </summary>
    public static bool IsServiceOwnedRoot(string? wellKnownName)
    {
        return wellKnownName != null &&
               (string.Equals(wellKnownName, DefaultRootWellKnownName, StringComparison.OrdinalIgnoreCase) ||
                wellKnownName.StartsWith(ReportingRootPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
