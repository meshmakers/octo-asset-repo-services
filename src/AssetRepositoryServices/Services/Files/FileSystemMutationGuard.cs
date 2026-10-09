using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     File system rules on the generic GraphQL mutations for System.Files entities (AB#6171 S2, design
///     §4.2). The generic create/update/delete resolvers call it before they apply their changes, whenever a
///     System.Files entity is written or a <c>System/ParentChild</c> association touches one:
///     <list type="bullet">
///         <item>creating a folder root requires the role <c>FileManagement</c>; its well-known name must be
///         unique and not reserved (route segments of the REST bytes API, the Reporting prefix);</item>
///         <item>names are unique per parent (case-insensitive) on create, rename and move;</item>
///         <item>every folder and file has at most one parent; a move removes the current parent in the same
///         update; roots never get a parent; a folder cannot move into itself or below itself;</item>
///         <item>roots owned by a service (<c>Files</c>, <c>ReportingAssets_*</c>) or a blueprint cannot be
///         renamed or deleted;</item>
///         <item>deleting a root or folder deletes everything below it (up to
///         <see cref="FilesOptions.MaxDeleteEntries" /> entries per mutation), and file system entries are
///         always erased (their GridFS bytes go with them).</item>
///     </list>
///     Name conflicts are checked over an unfiltered session (entries the caller cannot see still block a
///     name); the target folder must be visible to the caller. The writes stay in the caller's session, so
///     the engine's data-permission write guard decides per entity — a cascade that reaches an entry the
///     caller may not delete fails as a whole and leaves nothing behind.
/// </summary>
public class FileSystemMutationGuard(FileSystemService fileSystem, IOptions<FilesOptions> filesOptions)
{
    /// <summary>
    ///     Role required to create a folder root.
    /// </summary>
    public const string FileManagementRole = "FileManagement";

    private const string RtBlueprintSourceAttribute = "RtBlueprintSource";

    /// <summary>
    ///     True when a mutation of <paramref name="ckTypeId" /> with these associations must pass the guard.
    /// </summary>
    public static bool Applies(RtCkId<CkTypeId>? ckTypeId, IEnumerable<AssociationUpdateInfo> associations)
    {
        return FileSystemService.IsFileSystemType(ckTypeId) || associations.Any(TouchesFileSystem);
    }

    /// <summary>
    ///     True when an id list of a delete contains System.Files entities.
    /// </summary>
    public static bool Applies(IEnumerable<RtEntityId> ids) =>
        ids.Any(id => FileSystemService.IsFileSystemType(id.CkTypeId));

    /// <summary>
    ///     Validates inserts of System.Files entities and the parent changes of the batch.
    /// </summary>
    public async Task BeforeCreateAsync(ITenantRepository repository, IOctoSession session,
        RtSecurityContext securityContext, IReadOnlyList<EntityUpdateInfo<RtEntity>> inserts,
        IReadOnlyList<AssociationUpdateInfo> associations)
    {
        using var unfiltered = repository.GetSession();
        var insertedIds = inserts.Where(i => i.RtEntity != null).Select(i => i.RtEntity!.RtId).ToHashSet();
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

                await EnsureRootWellKnownNameFreeAsync(repository, unfiltered, securityContext, wellKnownName, null)
                    .ConfigureAwait(false);
                if (!newRootNames.Add(wellKnownName))
                {
                    throw FileSystemException.RootExists(wellKnownName);
                }

                if (ParentChanges(associations, entity.RtId).Any() || ChildChanges(associations, entity.RtId).Any())
                {
                    throw FileSystemException.InvalidRequest(
                        "Create the folder root first, then add folders and files to it.");
                }

                continue;
            }

            var creates = ParentChanges(associations, entity.RtId)
                .Where(a => a.ModOption == AssociationModOptionsDto.Create).ToList();
            if (creates.Count > 1)
            {
                throw FileSystemException.SingleParent(name!);
            }

            if (ChildChanges(associations, entity.RtId).Any())
            {
                throw FileSystemException.InvalidRequest("Create the folder first, then move entries into it.");
            }

            if (creates.Count == 0)
            {
                continue;
            }

            var parentId = creates[0].Target;
            await EnsureVisibleContainerAsync(repository, session, parentId).ConfigureAwait(false);
            await EnsureNameFreeAsync(repository, unfiltered, parentId, name!, null).ConfigureAwait(false);
            if (!namesPerParent.TryGetValue(parentId, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                namesPerParent[parentId] = names;
            }

            if (!names.Add(name!))
            {
                throw FileSystemException.NameConflict(name!);
            }
        }

        // Existing entries pulled into the tree by this batch (e.g. inbound "children" navigation).
        await CheckExistingEntriesAsync(repository, session, unfiltered, securityContext, [], associations,
            insertedIds).ConfigureAwait(false);
    }

    /// <summary>
    ///     Validates updates of System.Files entities (rename, change of the well-known name, move) and every
    ///     parent change of the batch.
    /// </summary>
    public async Task BeforeUpdateAsync(ITenantRepository repository, IOctoSession session,
        RtSecurityContext securityContext, IReadOnlyList<EntityUpdateInfo<RtEntity>> updates,
        IReadOnlyList<AssociationUpdateInfo> associations)
    {
        using var unfiltered = repository.GetSession();
        await CheckExistingEntriesAsync(repository, session, unfiltered, securityContext, updates, associations,
            new HashSet<OctoObjectId>())
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Refuses runtime-query row mutations that would write System.Files entities or their tree: the file
    ///     system rules are enforced on the typed and generic entity mutations only.
    /// </summary>
    public static void EnsureNotInQueryMutation(IEnumerable<RtCkId<CkTypeId>> ckTypeIds,
        IEnumerable<AssociationUpdateInfo> associations)
    {
        if (ckTypeIds.Any(FileSystemService.IsFileSystemType) || associations.Any(TouchesFileSystem))
        {
            throw FileSystemException.InvalidRequest(
                "Files and folders cannot be changed through a runtime query; use the entity mutations of System.Files.");
        }
    }

    /// <summary>
    ///     Expands a delete: every System.Files root or folder brings everything below it. Returns the
    ///     System.Files entities to erase (incl. the requested ones); the other ids stay with the caller.
    ///     Throws for protected roots and for cascades above <see cref="FilesOptions.MaxDeleteEntries" />.
    /// </summary>
    public async Task<IReadOnlyList<RtEntityId>> ExpandDeleteAsync(ITenantRepository repository,
        IReadOnlyList<RtEntityId> requested, CancellationToken cancellationToken = default)
    {
        using var unfiltered = repository.GetSession();
        var max = filesOptions.Value.MaxDeleteEntries;
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
            if (entry.IsContainer)
            {
                var walk = await fileSystem.WalkAsync(repository, unfiltered, entry, max + 1, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var descendant in walk.Items)
                {
                    if (seen.Add(descendant.Entry.Id.RtId))
                    {
                        result.Add(descendant.Entry.Id);
                    }
                }
            }

            if (result.Count > max)
            {
                // GridFS deletes do not take part in the mutation transaction: a cascade must stay well
                // inside the transaction lifetime, or an abort would leave entries without their bytes.
                throw FileSystemException.DeleteTooLarge(entry.Name, max);
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

    // ---------------------------------------------------------------------------------------------

    private async Task CheckExistingEntriesAsync(ITenantRepository repository, IOctoSession session,
        IOctoSession unfiltered, RtSecurityContext securityContext,
        IReadOnlyList<EntityUpdateInfo<RtEntity>> updates, IReadOnlyList<AssociationUpdateInfo> associations,
        IReadOnlySet<OctoObjectId> insertedIds)
    {
        var documents = updates
            .Where(u => FileSystemService.IsFileSystemType(u.CkTypeId) && u.RtId != null)
            .GroupBy(u => u.RtId!.Value)
            .ToDictionary(g => g.Key, g => g.First().RtEntity);

        var touched = documents.Keys
            .Concat(associations.Where(a => IsParentChild(a) && FileSystemService.IsFileSystemType(a.Origin.CkTypeId))
                .Select(a => a.Origin.RtId))
            .Where(id => !insertedIds.Contains(id))
            .Distinct()
            .ToList();

        // Containers that receive or lose children through the "children" navigation of a non-files type are
        // covered as well: their associations carry a System.Files origin or target.
        foreach (var association in associations.Where(a => IsParentChild(a) &&
                     !FileSystemService.IsFileSystemType(a.Origin.CkTypeId) &&
                     FileSystemService.IsFileSystemType(a.Target.CkTypeId)))
        {
            throw FileSystemException.InvalidRequest(
                $"'{FileSystemService.SemanticName(association.Origin.CkTypeId)}' cannot be placed in a folder.");
        }

        foreach (var rtId in touched)
        {
            var current = await fileSystem.FindByRtIdAsync(repository, unfiltered, rtId).ConfigureAwait(false);
            if (current == null)
            {
                continue; // the engine answers the missing entity
            }

            documents.TryGetValue(rtId, out var document);
            var newName = document?.GetAttributeStringValueOrDefault(nameof(FileSystemAttributeNames.Name));
            if (newName != null)
            {
                FileSystemNames.Validate(newName);
            }

            var changes = ParentChanges(associations, rtId).ToList();
            var creates = changes.Where(c => c.ModOption == AssociationModOptionsDto.Create).ToList();
            var deletes = changes.Where(c => c.ModOption == AssociationModOptionsDto.Delete).Select(c => c.Target.RtId)
                .ToHashSet();

            if (current.Kind == FileSystemEntryKind.Root)
            {
                if (creates.Count > 0)
                {
                    throw FileSystemException.InvalidRequest("A folder root cannot be placed inside a folder.");
                }

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
                    await EnsureRootWellKnownNameFreeAsync(repository, unfiltered, securityContext, newWellKnownName!,
                        current.Id.RtId).ConfigureAwait(false);
                }

                continue;
            }

            if (creates.Count > 1)
            {
                throw FileSystemException.SingleParent(current.Name);
            }

            var currentParents = await fileSystem.GetParentIdsAsync(repository, unfiltered, current.Id)
                .ConfigureAwait(false);
            var remaining = currentParents.Where(p => !deletes.Contains(p.RtId)).ToList();
            if (creates.Count == 1 && remaining.Any(p => p.RtId != creates[0].Target.RtId))
            {
                throw FileSystemException.SingleParent(current.Name);
            }

            var effectiveName = newName ?? current.Name;
            if (creates.Count == 1)
            {
                var target = creates[0].Target;
                if (insertedIds.Contains(target.RtId))
                {
                    throw FileSystemException.InvalidRequest("Create the folder first, then move entries into it.");
                }

                await EnsureVisibleContainerAsync(repository, session, target).ConfigureAwait(false);
                if (current.Kind == FileSystemEntryKind.Folder)
                {
                    await EnsureNotOwnDescendantAsync(repository, unfiltered, current, target).ConfigureAwait(false);
                }

                await EnsureNameFreeAsync(repository, unfiltered, target, effectiveName, current.Id.RtId)
                    .ConfigureAwait(false);
            }
            else if (newName != null && newName != current.Name)
            {
                foreach (var parentId in remaining)
                {
                    await EnsureNameFreeAsync(repository, unfiltered, parentId, effectiveName, current.Id.RtId)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static bool TouchesFileSystem(AssociationUpdateInfo association) =>
        IsParentChild(association) &&
        (FileSystemService.IsFileSystemType(association.Origin.CkTypeId) ||
         FileSystemService.IsFileSystemType(association.Target.CkTypeId));

    private static bool IsParentChild(AssociationUpdateInfo association) =>
        string.Equals(association.RoleId.ModelId + "/" + association.RoleId.ElementId.RoleId,
            FileSystemConstants.ParentChildRoleId, StringComparison.Ordinal);

    private static IEnumerable<AssociationUpdateInfo> ParentChanges(IEnumerable<AssociationUpdateInfo> associations,
        OctoObjectId childRtId) =>
        associations.Where(a => IsParentChild(a) && a.Origin.RtId == childRtId);

    private static IEnumerable<AssociationUpdateInfo> ChildChanges(IEnumerable<AssociationUpdateInfo> associations,
        OctoObjectId parentRtId) =>
        associations.Where(a => IsParentChild(a) && a.Target.RtId == parentRtId);

    /// <summary>
    ///     The target folder must exist and be visible to the caller (no placement in folders the caller
    ///     cannot see, no existence oracle).
    /// </summary>
    private async Task EnsureVisibleContainerAsync(ITenantRepository repository, IOctoSession session,
        RtEntityId parentId)
    {
        var kind = FileSystemService.KindOf(parentId.CkTypeId);
        var parent = kind is FileSystemEntryKind.Root or FileSystemEntryKind.Folder
            ? await fileSystem.FindByRtIdAsync(repository, session, parentId.RtId).ConfigureAwait(false)
            : null;
        if (parent == null || !parent.IsContainer)
        {
            throw FileSystemException.NotAFolder(parentId.RtId.ToString());
        }
    }

    private async Task EnsureNameFreeAsync(ITenantRepository repository, IOctoSession session,
        RtEntityId parentId, string name, OctoObjectId? self)
    {
        var siblings = await fileSystem.GetChildrenByNameAsync(repository, session, parentId, name).ConfigureAwait(false);
        if (siblings.Any(s => s.Id.RtId != self))
        {
            throw FileSystemException.NameConflict(name);
        }
    }

    private async Task EnsureRootWellKnownNameFreeAsync(ITenantRepository repository, IOctoSession session,
        RtSecurityContext securityContext, string wellKnownName, OctoObjectId? self)
    {
        FileSystemNames.Validate(wellKnownName);
        if (FileSystemConstants.ReservedRootWellKnownNames.Contains(wellKnownName) ||
            (!securityContext.IsSystem &&
             wellKnownName.StartsWith(FileSystemConstants.ReportingRootPrefix, StringComparison.OrdinalIgnoreCase)))
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

    private async Task EnsureNotOwnDescendantAsync(ITenantRepository repository, IOctoSession session,
        FileSystemEntry folder, RtEntityId targetParent)
    {
        var visited = new HashSet<OctoObjectId>();
        var level = new List<RtEntityId> { targetParent };
        while (level.Count > 0)
        {
            var next = new List<RtEntityId>();
            foreach (var current in level)
            {
                if (current.RtId == folder.Id.RtId)
                {
                    throw FileSystemException.MoveIntoItself(folder.Name);
                }

                if (!visited.Add(current.RtId))
                {
                    continue;
                }

                next.AddRange(await fileSystem.GetParentIdsAsync(repository, session, current).ConfigureAwait(false));
            }

            level = next;
        }
    }
}
