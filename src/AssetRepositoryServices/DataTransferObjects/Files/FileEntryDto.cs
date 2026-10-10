namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;

/// <summary>
///     A file or folder of the platform file system as answered by the REST file API (AB#6171).
/// </summary>
public class FileEntryDto
{
    /// <summary>Runtime id of the entity.</summary>
    public required string RtId { get; init; }

    /// <summary>Type id (System.Files/FileSystemItem, System.Files/Folder, System.Files/FolderRoot).</summary>
    public required string CkTypeId { get; init; }

    /// <summary>"file", "folder" or "root".</summary>
    public required string Kind { get; init; }

    /// <summary>Name of the entry.</summary>
    public required string Name { get; init; }

    /// <summary>Well-known name of the root the entry was addressed in, when known.</summary>
    public string? Root { get; init; }

    /// <summary>Path below the root ('/'-separated, incl. the name), when known.</summary>
    public string? Path { get; init; }

    /// <summary>Runtime id of the parent folder or root, when known.</summary>
    public string? ParentRtId { get; init; }

    /// <summary>Size in bytes (files).</summary>
    public long? Size { get; init; }

    /// <summary>Content type (files).</summary>
    public string? ContentType { get; init; }

    /// <summary>GridFS id of the content (files).</summary>
    public string? BinaryId { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>Last change (UTC).</summary>
    public DateTime? ChangedAt { get; init; }

    /// <summary>Subject that created the entry.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>True when an upload replaced the content of an existing file.</summary>
    public bool Replaced { get; init; }
}
