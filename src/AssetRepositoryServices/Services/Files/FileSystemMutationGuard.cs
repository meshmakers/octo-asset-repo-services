using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     File system rules on the generic GraphQL mutations for System.Files entities (AB#6171 S2, design
///     §4.2). The generic create/update/delete resolvers call it before they apply their changes:
///     <list type="bullet">
///         <item>creating a folder root requires the role <c>FileManagement</c>; its well-known name must be
///         unique and not reserved by the REST bytes API;</item>
///         <item>names are unique per parent (case-insensitive) on create, rename and move;</item>
///         <item>roots owned by a service (<c>Files</c>, <c>ReportingAssets_*</c>) or a blueprint cannot be
///         renamed or deleted;</item>
///         <item>a folder cannot be moved into itself or below itself;</item>
///         <item>deleting a root or folder deletes everything below it, and file system entries are always
///         erased (their GridFS bytes go with them).</item>
///     </list>
///     Conflicts are checked over an unfiltered session (entries the caller cannot see still block a
///     name); the writes themselves stay in the caller's session, so the engine's data-permission write
///     guard decides per entity — a cascade that reaches an entry the caller may not delete fails as a
///     whole and leaves nothing behind.
/// </summary>
public class FileSystemMutationGuard(FileSystemService fileSystem)
{
    /// <summary>
    ///     Role required to create a folder root.
    /// </summary>
    public const string FileManagementRole = "FileManagement";

    private const string RtBlueprintSourceAttribute = "RtBlueprintSource";

    /// <summary>
    ///     Validates inserts of System.Files entities.
    /// </summary>
    public async Task BeforeCreateAsync(ITenantRepository repository, RtSecurityContext securityContext,
        IReadOnlyList<EntityUpdateInfo<RtEntity>> inserts, IReadOnlyList<AssociationUpdateInfo> associations)
    {
        using var unfiltered = repository.GetSession();
        var namesPerParent = new Dictionary<RtEntityId, HashSet<string>>();
        var newRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var insert in inserts)
        {
            var kind = FileSystemService.KindOf(insert.CkTypeId);
            if (kind == null || insert.RtEntity == null)
            {
                continue;
            }

            var entity = insert.RtEntity;
            var name = entity.GetAttributeStringValueOrDefault(nameof(FileSystemAttributeNames.Name));
            FileSystemNames.Validate(name);

            if (kind == FileSystemEntryKind.Root)
            {
                if (!securityContext.IsSystem &&
                    !securityContext.Roles.Contains(FileManagementRole, StringComparer.OrdinalIgnoreCase))
                {
                    throw FileSystemException.RootCreationForbidden();
                }

                var wellKnownName = entity.RtWellKnownName;
                if (string.IsNullOrWhiteSpace(wellKnownName))
                {
                    throw FileSystemException.InvalidRequest("A folder root needs a well-known name (rtWellKnownName).");
                }

                await EnsureRootWellKnownNameFreeAsync(repository, unfiltered, wellKnownName, null).ConfigureAwait(false);
                if (!newRootNames.Add(wellKnownName))
                {
                    throw FileSystemException.RootExists(wellKnownName);
                }

                continue;
            }

            var parentId = FindParentChange(associations, entity.RtId, AssociationModOptionsDto.Create);
            if (parentId == null)
            {
                continue;
            }

            await EnsureContainerAsync(repository, unfiltered, parentId.Value).ConfigureAwait(false);
            await EnsureNameFreeAsync(repository, unfiltered, parentId.Value, name!, null).ConfigureAwait(false);
            if (!namesPerParent.TryGetValue(parentId.Value, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                namesPerParent[parentId.Value] = names;
            }

            if (!names.Add(name!))
            {
                throw FileSystemException.NameConflict(name!);
            }
        }
    }

    /// <summary>
    ///     Validates updates of System.Files entities (rename, change of the well-known name, move).
    /// </summary>
    public async Task BeforeUpdateAsync(ITenantRepository repository,
        IReadOnlyList<EntityUpdateInfo<RtEntity>> updates, IReadOnlyList<AssociationUpdateInfo> associations)
    {
        using var unfiltered = repository.GetSession();
        var handled = new HashSet<OctoObjectId>();

        // Entities that only appear through an association change (a move without attribute changes is
        // still an update entry in the generic resolvers, but be defensive).
        var touched = updates
            .Where(u => FileSystemService.IsFileSystemType(u.CkTypeId) && u.RtId != null)
            .Select(u => (Id: new RtEntityId(u.CkTypeId, u.RtId!.Value), u.RtEntity))
            .Concat(associations
                .Where(a => IsParentChild(a) && FileSystemService.IsFileSystemType(a.Origin.CkTypeId))
                .Select(a => (Id: a.Origin, RtEntity: (RtEntity?)null)))
            .ToList();

        foreach (var (id, document) in touched)
        {
            if (!handled.Add(id.RtId))
            {
                continue;
            }

            var current = await fileSystem.FindByRtIdAsync(repository, unfiltered, id.RtId).ConfigureAwait(false);
            if (current == null)
            {
                continue; // the engine answers the missing entity
            }

            var newName = document?.GetAttributeStringValueOrDefault(nameof(FileSystemAttributeNames.Name));
            if (newName != null)
            {
                FileSystemNames.Validate(newName);
            }

            if (current.Kind == FileSystemEntryKind.Root)
            {
                var newWellKnownName = document?.RtWellKnownName;
                var renames = newName != null && newName != current.Name;
                var rekeys = newWellKnownName != null &&
                             !string.Equals(newWellKnownName, current.Entity.RtWellKnownName, StringComparison.Ordinal);
                if ((renames || rekeys) && IsProtectedRoot(current))
                {
                    throw FileSystemException.ProtectedRoot(current.Entity.RtWellKnownName ?? current.Name);
                }

                if (rekeys)
                {
                    await EnsureRootWellKnownNameFreeAsync(repository, unfiltered, newWellKnownName!, current.Id.RtId)
                        .ConfigureAwait(false);
                }

                if (FindParentChange(associations, id.RtId, AssociationModOptionsDto.Create) != null)
                {
                    throw FileSystemException.InvalidRequest("A folder root cannot be placed inside a folder.");
                }

                continue;
            }

            var newParent = FindParentChange(associations, id.RtId, AssociationModOptionsDto.Create);
            var effectiveName = newName ?? current.Name;
            if (newParent == null && (newName == null || newName == current.Name))
            {
                continue;
            }

            var targetParents = newParent != null
                ? [newParent.Value]
                : await fileSystem.GetParentIdsAsync(repository, unfiltered, current.Id).ConfigureAwait(false);

            foreach (var parentId in targetParents)
            {
                if (newParent != null)
                {
                    await EnsureContainerAsync(repository, unfiltered, parentId).ConfigureAwait(false);
                    if (current.Kind == FileSystemEntryKind.Folder)
                    {
                        await EnsureNotOwnDescendantAsync(repository, unfiltered, current, parentId)
                            .ConfigureAwait(false);
                    }
                }

                await EnsureNameFreeAsync(repository, unfiltered, parentId, effectiveName, current.Id.RtId)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Expands a delete: every System.Files root or folder brings everything below it. Returns the
    ///     System.Files entities to erase (incl. the requested ones); the other ids stay with the caller.
    ///     Throws for protected roots and for trees above the walk limit.
    /// </summary>
    public async Task<IReadOnlyList<RtEntityId>> ExpandDeleteAsync(ITenantRepository repository,
        IReadOnlyList<RtEntityId> requested, CancellationToken cancellationToken = default)
    {
        using var unfiltered = repository.GetSession();
        var result = new List<RtEntityId>();
        var seen = new HashSet<OctoObjectId>();

        foreach (var id in requested.Where(r => FileSystemService.IsFileSystemType(r.CkTypeId)))
        {
            if (!seen.Add(id.RtId))
            {
                continue;
            }

            var entry = await fileSystem.FindByRtIdAsync(repository, unfiltered, id.RtId).ConfigureAwait(false);
            if (entry == null)
            {
                result.Add(id); // let the engine answer the missing entity
                continue;
            }

            if (entry.Kind == FileSystemEntryKind.Root && IsProtectedRoot(entry))
            {
                throw FileSystemException.ProtectedRoot(entry.Entity.RtWellKnownName ?? entry.Name);
            }

            result.Add(entry.Id);
            if (!entry.IsContainer)
            {
                continue;
            }

            var walk = await fileSystem.WalkAsync(repository, unfiltered, entry,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (walk.Truncated)
            {
                throw FileSystemException.InvalidRequest(
                    $"'{entry.Name}' contains more than {FileSystemService.DefaultWalkLimit} entries; delete its subfolders first.");
            }

            foreach (var descendant in walk.Items)
            {
                if (seen.Add(descendant.Entry.Id.RtId))
                {
                    result.Add(descendant.Entry.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     True for roots owned by a service (well-known name) or installed by a blueprint.
    /// </summary>
    public static bool IsProtectedRoot(FileSystemEntry root)
    {
        return FileSystemConstants.IsServiceOwnedRoot(root.Entity.RtWellKnownName) ||
               !string.IsNullOrEmpty(root.Entity.GetAttributeStringValueOrDefault(RtBlueprintSourceAttribute));
    }

    private static bool IsParentChild(AssociationUpdateInfo association) =>
        string.Equals(association.RoleId.ModelId + "/" + association.RoleId.ElementId.RoleId,
            FileSystemConstants.ParentChildRoleId, StringComparison.Ordinal);

    private static RtEntityId? FindParentChange(IReadOnlyList<AssociationUpdateInfo> associations,
        OctoObjectId childRtId, AssociationModOptionsDto modOption)
    {
        var match = associations.FirstOrDefault(a =>
            a.ModOption == modOption && IsParentChild(a) && a.Origin.RtId == childRtId);
        return match?.Target;
    }

    private async Task EnsureContainerAsync(ITenantRepository repository, Runtime.Contracts.IOctoSession session,
        RtEntityId parentId)
    {
        var kind = FileSystemService.KindOf(parentId.CkTypeId);
        if (kind is not (FileSystemEntryKind.Root or FileSystemEntryKind.Folder))
        {
            throw FileSystemException.NotAFolder(parentId.RtId.ToString());
        }

        var parent = await fileSystem.FindByRtIdAsync(repository, session, parentId.RtId).ConfigureAwait(false);
        if (parent == null || !parent.IsContainer)
        {
            throw FileSystemException.NotAFolder(parentId.RtId.ToString());
        }
    }

    private async Task EnsureNameFreeAsync(ITenantRepository repository, Runtime.Contracts.IOctoSession session,
        RtEntityId parentId, string name, OctoObjectId? self)
    {
        var siblings = await fileSystem.GetChildrenByNameAsync(repository, session, parentId, name).ConfigureAwait(false);
        if (siblings.Any(s => s.Id.RtId != self))
        {
            throw FileSystemException.NameConflict(name);
        }
    }

    private async Task EnsureRootWellKnownNameFreeAsync(ITenantRepository repository,
        Runtime.Contracts.IOctoSession session, string wellKnownName, OctoObjectId? self)
    {
        FileSystemNames.Validate(wellKnownName);
        if (FileSystemConstants.ReservedRootWellKnownNames.Contains(wellKnownName))
        {
            throw FileSystemException.ReservedRootName(wellKnownName);
        }

        var roots = await fileSystem.GetRootsAsync(repository, session).ConfigureAwait(false);
        if (roots.Any(r => r.Id.RtId != self &&
                           string.Equals(r.Entity.RtWellKnownName, wellKnownName, StringComparison.OrdinalIgnoreCase)))
        {
            throw FileSystemException.RootExists(wellKnownName);
        }
    }

    private async Task EnsureNotOwnDescendantAsync(ITenantRepository repository,
        Runtime.Contracts.IOctoSession session, FileSystemEntry folder, RtEntityId targetParent)
    {
        var current = targetParent;
        for (var depth = 0; depth < 1000; depth++)
        {
            if (current.RtId == folder.Id.RtId)
            {
                throw FileSystemException.MoveIntoItself(folder.Name);
            }

            var parents = await fileSystem.GetParentIdsAsync(repository, session, current).ConfigureAwait(false);
            if (parents.Count == 0)
            {
                return;
            }

            current = parents[0];
        }
    }
}
