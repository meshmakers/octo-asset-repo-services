using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Kind of a file system entry.
/// </summary>
public enum FileSystemEntryKind
{
    /// <summary>A folder root (System.Files/FolderRoot).</summary>
    Root,

    /// <summary>A folder (System.Files/Folder).</summary>
    Folder,

    /// <summary>A file (System.Files/FileSystemItem).</summary>
    File
}

/// <summary>
///     One entry of the file system tree.
/// </summary>
public sealed class FileSystemEntry
{
    internal FileSystemEntry(RtEntity entity, FileSystemEntryKind kind)
    {
        Entity = entity;
        Kind = kind;
    }

    /// <summary>The stored entity.</summary>
    public RtEntity Entity { get; }

    /// <summary>Kind of the entry.</summary>
    public FileSystemEntryKind Kind { get; }

    /// <summary>Id of the entity.</summary>
    public RtEntityId Id => new(Entity.CkTypeId!, Entity.RtId);

    /// <summary>Name attribute of the entity.</summary>
    public string Name => Entity.GetAttributeStringValueOrDefault(nameof(FileSystemAttributeNames.Name)) ?? string.Empty;

    /// <summary>Linked binary of a file, otherwise null.</summary>
    public EntityBinaryInfo? Content => Kind == FileSystemEntryKind.File
        ? Entity.GetAttributeLinkedBinaryValueOrDefault(nameof(FileSystemAttributeNames.Content))
        : null;

    /// <summary>True for roots and folders.</summary>
    public bool IsContainer => Kind != FileSystemEntryKind.File;
}

/// <summary>
///     Attribute names of System.Files (PascalCase, as stored on <see cref="RtEntity" />).
/// </summary>
internal enum FileSystemAttributeNames
{
    Name,
    Content
}

/// <summary>
///     A descendant of a folder together with its path relative to the folder it was found from.
/// </summary>
/// <param name="Entry">The entry</param>
/// <param name="RelativePath">Path below the start folder, '/'-separated, including the entry's own name</param>
/// <param name="ParentId">Id of the parent folder</param>
public sealed record FileSystemDescendant(FileSystemEntry Entry, string RelativePath, RtEntityId ParentId);

/// <summary>
///     Result of a descendant walk.
/// </summary>
/// <param name="Items">Descendants found (breadth first)</param>
/// <param name="Truncated">True when the walk stopped at its limit</param>
public sealed record FileSystemWalk(IReadOnlyList<FileSystemDescendant> Items, bool Truncated);

/// <summary>
///     How an upload treats an existing file with the same name.
/// </summary>
public enum FileConflictMode
{
    /// <summary>Refuse with NAME_CONFLICT (409).</summary>
    Fail,

    /// <summary>Replace the content of the existing file (rtId and links stay).</summary>
    Replace,

    /// <summary>Store under the next free name ("a (1).pdf").</summary>
    KeepBoth
}
