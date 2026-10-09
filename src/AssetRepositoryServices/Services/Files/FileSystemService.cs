using System.Text.RegularExpressions;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Tree operations of the platform file system (AB#6171): path resolution, children, parents,
///     descendant walks, uploads and deletes on System.Files entities. Every read runs through the session
///     it is given — a user session applies the caller's data permissions (hidden entries simply do not
///     exist for it), an unfiltered session sees everything (name conflicts, delete cascades).
/// </summary>
public class FileSystemService
{
    /// <summary>
    ///     Upper bound of a descendant walk (stats, zip expansion, delete cascade).
    /// </summary>
    public const int DefaultWalkLimit = 50_000;

    private const int OriginBatchSize = 200;

    internal static readonly RtCkId<CkTypeId> FolderRootType = new(FileSystemConstants.FolderRootCkTypeId);
    internal static readonly RtCkId<CkTypeId> FolderType = new(FileSystemConstants.FolderCkTypeId);
    internal static readonly RtCkId<CkTypeId> FileType = new(FileSystemConstants.FileSystemItemCkTypeId);
    internal static readonly RtCkId<CkAssociationRoleId> ParentChildRole = new(FileSystemConstants.ParentChildRoleId);

    private static readonly RtCkId<CkTypeId>[] ContainerChildTypes = [FolderType, FileType];

    /// <summary>
    ///     True when the type id belongs to the System.Files model.
    /// </summary>
    public static bool IsFileSystemType(RtCkId<CkTypeId>? ckTypeId)
    {
        return ckTypeId != null && string.Equals(ckTypeId.ModelId, FileSystemConstants.ModelName, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Kind of a System.Files type, or null for any other type (incl. the abstract ones).
    /// </summary>
    public static FileSystemEntryKind? KindOf(RtCkId<CkTypeId>? ckTypeId)
    {
        return (ckTypeId == null ? null : SemanticName(ckTypeId)) switch
        {
            FileSystemConstants.FolderRootCkTypeId => FileSystemEntryKind.Root,
            FileSystemConstants.FolderCkTypeId => FileSystemEntryKind.Folder,
            FileSystemConstants.FileSystemItemCkTypeId => FileSystemEntryKind.File,
            _ => null
        };
    }

    /// <summary>
    ///     "Model/Type" without versions (the form stored in <c>ckTypeId</c>).
    /// </summary>
    public static string SemanticName(RtCkId<CkTypeId> ckTypeId) => $"{ckTypeId.ModelId}/{ckTypeId.ElementId.Name}";

    internal static FileSystemEntry? ToEntry(RtEntity entity)
    {
        var kind = KindOf(entity.CkTypeId);
        return kind == null ? null : new FileSystemEntry(entity, kind.Value);
    }

    /// <summary>
    ///     The folder root with the given well-known name, or null.
    /// </summary>
    public async Task<FileSystemEntry?> FindRootAsync(ITenantRepository repository, IOctoSession session,
        string wellKnownName)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldEquals(nameof(RtEntity.RtWellKnownName), wellKnownName);
        var result = await repository.GetRtEntitiesByTypeAsync(session, FolderRootType, options).ConfigureAwait(false);
        var root = result.Items.FirstOrDefault();
        return root == null ? null : new FileSystemEntry(root, FileSystemEntryKind.Root);
    }

    /// <summary>
    ///     All folder roots visible to the session.
    /// </summary>
    public async Task<IReadOnlyList<FileSystemEntry>> GetRootsAsync(ITenantRepository repository,
        IOctoSession session)
    {
        var result = await repository.GetRtEntitiesByTypeAsync(session, FolderRootType, RtEntityQueryOptions.Create())
            .ConfigureAwait(false);
        return result.Items.Select(r => new FileSystemEntry(r, FileSystemEntryKind.Root)).ToList();
    }

    /// <summary>
    ///     Resolves <c>root</c> + <c>path</c> to an entry; an empty path answers the root itself. A segment
    ///     matches its entry by exact name first, then case-insensitively; more than one match answers
    ///     <c>AMBIGUOUS_PATH</c>, none <c>PATH_NOT_FOUND</c>.
    /// </summary>
    public async Task<FileSystemEntry> ResolveAsync(ITenantRepository repository, IOctoSession session,
        string root, string? path)
    {
        var current = await FindRootAsync(repository, session, root).ConfigureAwait(false)
                      ?? throw FileSystemException.RootNotFound(root);

        var segments = FileSystemNames.SplitPath(path);
        for (var i = 0; i < segments.Count; i++)
        {
            var displayPath = $"{root}/{FileSystemNames.JoinPath(segments.Take(i + 1))}";
            if (!current.IsContainer)
            {
                throw FileSystemException.PathNotFound(displayPath);
            }

            var matches = await GetChildrenByNameAsync(repository, session, current.Id, segments[i])
                .ConfigureAwait(false);
            var exact = matches.Where(m => string.Equals(m.Name, segments[i], StringComparison.Ordinal)).ToList();
            var candidates = exact.Count > 0 ? exact : matches;
            if (candidates.Count == 0)
            {
                throw FileSystemException.PathNotFound(displayPath);
            }

            if (candidates.Count > 1)
            {
                throw FileSystemException.AmbiguousPath(displayPath);
            }

            current = candidates[0];
        }

        return current;
    }

    /// <summary>
    ///     A file system entry by rtId (any of the three concrete types), or null.
    /// </summary>
    public async Task<FileSystemEntry?> FindByRtIdAsync(ITenantRepository repository, IOctoSession session,
        OctoObjectId rtId)
    {
        foreach (var type in new[] { FileType, FolderType, FolderRootType })
        {
            var result = await repository.GetRtEntitiesByIdAsync(session, type, [rtId], RtEntityQueryOptions.Create())
                .ConfigureAwait(false);
            var entity = result.Items.FirstOrDefault();
            if (entity != null)
            {
                return ToEntry(entity);
            }
        }

        return null;
    }

    /// <summary>
    ///     Children (folders and files) of a root or folder whose name equals <paramref name="name" />
    ///     case-insensitively.
    /// </summary>
    public async Task<IReadOnlyList<FileSystemEntry>> GetChildrenByNameAsync(ITenantRepository repository,
        IOctoSession session, RtEntityId parentId, string name)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldMatchRegex(nameof(FileSystemAttributeNames.Name), CaseInsensitiveExact(name));
        return await GetChildrenAsync(repository, session, parentId, options).ConfigureAwait(false);
    }

    /// <summary>
    ///     All children (folders and files) of a root or folder.
    /// </summary>
    public async Task<IReadOnlyList<FileSystemEntry>> GetChildrenAsync(ITenantRepository repository,
        IOctoSession session, RtEntityId parentId, RtEntityQueryOptions? options = null)
    {
        var result = await repository.GetRtAssociationTargetsAsync(session, [parentId.RtId], parentId.CkTypeId,
                ParentChildRole, ContainerChildTypes, GraphDirections.Inbound, null,
                options ?? RtEntityQueryOptions.Create())
            .ConfigureAwait(false);
        return result.TryGetValue(parentId, out var children)
            ? children.Items.Select(ToEntry).OfType<FileSystemEntry>().ToList()
            : [];
    }

    /// <summary>
    ///     Ids of the parents (folder or root) of an entry; normally zero or one.
    /// </summary>
    public async Task<IReadOnlyList<RtEntityId>> GetParentIdsAsync(ITenantRepository repository,
        IOctoSession session, RtEntityId entryId)
    {
        var result = await repository.GetRtAssociationsAsync(session, entryId,
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, ParentChildRole))
            .ConfigureAwait(false);
        return result.Items
            .Where(a => a.OriginRtId == entryId.RtId)
            .Select(a => new RtEntityId(a.TargetCkTypeId, a.TargetRtId))
            .ToList();
    }

    /// <summary>
    ///     Walks every descendant of the given containers breadth first, up to <paramref name="limit" />
    ///     entries. Paths are relative to the start container, so the caller decides whether to prefix the
    ///     container's own name.
    /// </summary>
    public async Task<FileSystemWalk> WalkAsync(ITenantRepository repository, IOctoSession session,
        FileSystemEntry start, int limit = DefaultWalkLimit, CancellationToken cancellationToken = default)
    {
        var items = new List<FileSystemDescendant>();
        if (!start.IsContainer)
        {
            return new FileSystemWalk(items, false);
        }

        var level = new List<(FileSystemEntry Entry, string Path)> { (start, string.Empty) };
        while (level.Count > 0)
        {
            var next = new List<(FileSystemEntry Entry, string Path)>();
            foreach (var group in level.GroupBy(l => SemanticName(l.Entry.Id.CkTypeId)))
            {
                foreach (var chunk in group.Chunk(OriginBatchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var originType = chunk[0].Entry.Id.CkTypeId;
                    var result = await repository.GetRtAssociationTargetsAsync(session,
                            chunk.Select(c => c.Entry.Id.RtId).ToList(), originType, ParentChildRole,
                            ContainerChildTypes, GraphDirections.Inbound, null, RtEntityQueryOptions.Create())
                        .ConfigureAwait(false);

                    foreach (var (parent, parentPath) in chunk)
                    {
                        if (!result.TryGetValue(parent.Id, out var children))
                        {
                            continue;
                        }

                        foreach (var child in children.Items.Select(ToEntry).OfType<FileSystemEntry>())
                        {
                            var path = parentPath.Length == 0 ? child.Name : $"{parentPath}/{child.Name}";
                            items.Add(new FileSystemDescendant(child, path, parent.Id));
                            if (items.Count >= limit)
                            {
                                return new FileSystemWalk(items, true);
                            }

                            if (child.IsContainer)
                            {
                                next.Add((child, path));
                            }
                        }
                    }
                }
            }

            level = next;
        }

        return new FileSystemWalk(items, false);
    }

    /// <summary>
    ///     Stores a file in a folder or root. The stream must be seekable (the engine reads its length).
    ///     Name conflicts are detected over <paramref name="unfilteredSession" /> (entries the caller cannot see
    ///     still block the name); the write itself runs in <paramref name="session" />, so the engine's data
    ///     permission write guard applies.
    /// </summary>
    public async Task<(FileSystemEntry Entry, bool Replaced)> UploadAsync(ITenantRepository repository,
        IOctoSession session, IOctoSession unfilteredSession, FileSystemEntry parent, string name,
        string contentType, Stream content, FileConflictMode conflict)
    {
        FileSystemNames.Validate(name);
        if (!parent.IsContainer)
        {
            throw FileSystemException.NotAFolder(parent.Name);
        }

        var existing = await GetChildrenByNameAsync(repository, unfilteredSession, parent.Id, name)
            .ConfigureAwait(false);
        var targetName = name;
        if (existing.Count > 0)
        {
            switch (conflict)
            {
                case FileConflictMode.Replace when existing.Count == 1 && existing[0].Kind == FileSystemEntryKind.File:
                    return (await ReplaceContentAsync(repository, session, existing[0], contentType, content)
                        .ConfigureAwait(false), true);
                case FileConflictMode.KeepBoth:
                    var siblings = await GetChildrenAsync(repository, unfilteredSession, parent.Id)
                        .ConfigureAwait(false);
                    var taken = new HashSet<string>(siblings.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                    targetName = FileSystemNames.NextFreeName(name, taken.Contains);
                    break;
                default:
                    throw FileSystemException.NameConflict(name);
            }
        }

        var item = await repository.CreateTransientRtEntityAsync<RtFileSystemItem>().ConfigureAwait(false);
        item.Name = targetName;
        item.SetAttributeValue(nameof(FileSystemAttributeNames.Content), AttributeValueTypesDto.BinaryLinked,
            new EntityBinaryInfo
            {
                Filename = targetName,
                ContentType = contentType,
                Size = content.Length,
                Stream = content
            });
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(FileType, item)],
            [AssociationUpdateInfo.CreateInsert(new RtEntityId(FileType, item.RtId), parent.Id, ParentChildRole)],
            operationResult).ConfigureAwait(false);
        GraphQL.Utils.ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

        var stored = await FindByRtIdAsync(repository, session, item.RtId).ConfigureAwait(false)
                     ?? throw FileSystemException.ItemNotFound(item.RtId.ToString());
        return (stored, false);
    }

    private async Task<FileSystemEntry> ReplaceContentAsync(ITenantRepository repository, IOctoSession session,
        FileSystemEntry existing, string contentType, Stream content)
    {
        // The engine's partial update does not upload linked binaries; a replace does (and deletes the
        // previous GridFS file). The stored document is carried over except for the content.
        var replacement = new RtEntity(existing.Id.CkTypeId, existing.Id.RtId)
        {
            RtWellKnownName = existing.Entity.RtWellKnownName
        };
        foreach (var attribute in existing.Entity.Attributes)
        {
            if (!string.Equals(attribute.Key, nameof(FileSystemAttributeNames.Content),
                    StringComparison.OrdinalIgnoreCase))
            {
                replacement.SetAttributeRawValue(attribute.Key, attribute.Value);
            }
        }

        replacement.SetAttributeValue(nameof(FileSystemAttributeNames.Content), AttributeValueTypesDto.BinaryLinked,
            new EntityBinaryInfo
            {
                Filename = existing.Name,
                ContentType = contentType,
                Size = content.Length,
                Stream = content
            });

        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateReplace(existing.Id, replacement)],
            [], operationResult).ConfigureAwait(false);
        GraphQL.Utils.ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

        return await FindByRtIdAsync(repository, session, existing.Id.RtId).ConfigureAwait(false)
               ?? throw FileSystemException.ItemNotFound(existing.Id.RtId.ToString());
    }

    internal static string CaseInsensitiveExact(string name) => $"(?i)^{Regex.Escape(name)}$";
}
