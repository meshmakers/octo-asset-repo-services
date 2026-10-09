using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6171 S2: file system rules on the generic GraphQL mutations, exercised with the documents the
///     Refinery Studio Files page sends (studio feat/gerald/ab6178-files-page, src/app/graphQL/files*.ts).
/// </summary>
[Collection(FilesCollection.Name)]
public class FileSystemMutationGuardTests
{
    private const string CreateRoot = """
        mutation filesCreateRoot($name: String!, $wellKnownName: String!) {
          runtime { systemFilesFolderRoots { create(entities: [{name: $name, rtWellKnownName: $wellKnownName}]) {
            rtId ckTypeId rtWellKnownName name } } }
        }
        """;

    private const string RenameRoot = """
        mutation filesRenameRoot($rtId: OctoObjectId!, $name: String!) {
          runtime { systemFilesFolderRoots { update(entities: [{rtId: $rtId, item: {name: $name}}]) { rtId name } } }
        }
        """;

    private const string CreateFolder = """
        mutation filesCreateFolder($name: String!, $parentCkTypeId: RtCkTypeId!, $parentRtId: OctoObjectId!) {
          runtime { systemFilesFolders { create(entities: [{name: $name,
            parent: [{modOption: CREATE, target: {ckTypeId: $parentCkTypeId, rtId: $parentRtId}}]}]) { rtId name } } }
        }
        """;

    private const string RenameFolder = """
        mutation filesRenameFolder($rtId: OctoObjectId!, $name: String!) {
          runtime { systemFilesFolders { update(entities: [{rtId: $rtId, item: {name: $name}}]) { rtId name } } }
        }
        """;

    private const string MoveFolder = """
        mutation filesMoveFolder($rtId: OctoObjectId!, $fromCkTypeId: RtCkTypeId!, $fromRtId: OctoObjectId!, $toCkTypeId: RtCkTypeId!, $toRtId: OctoObjectId!) {
          runtime { systemFilesFolders { update(entities: [{rtId: $rtId, item: {parent: [
            {modOption: DELETE, target: {ckTypeId: $fromCkTypeId, rtId: $fromRtId}},
            {modOption: CREATE, target: {ckTypeId: $toCkTypeId, rtId: $toRtId}}]}}]) { rtId } } }
        }
        """;

    private const string MoveItem = """
        mutation filesMoveItem($rtId: OctoObjectId!, $fromCkTypeId: RtCkTypeId!, $fromRtId: OctoObjectId!, $toCkTypeId: RtCkTypeId!, $toRtId: OctoObjectId!) {
          runtime { systemFilesFileSystemItems { update(entities: [{rtId: $rtId, item: {parent: [
            {modOption: DELETE, target: {ckTypeId: $fromCkTypeId, rtId: $fromRtId}},
            {modOption: CREATE, target: {ckTypeId: $toCkTypeId, rtId: $toRtId}}]}}]) { rtId } } }
        }
        """;

    private const string Delete = """
        mutation filesDelete($rtEntityIds: [RtEntityId]!) {
          runtime { runtimeEntities { delete(entities: $rtEntityIds) } }
        }
        """;

    private const string FolderChildren = """
        query filesFolderChildren($rtId: OctoObjectId!, $ckTypeIds: [String!]!, $first: Int, $after: String) {
          runtime { systemFilesFolder(rtId: $rtId) { items { rtId
            children(ckTypeIds: $ckTypeIds, first: $first, after: $after,
                     sortOrder: [{attributePath: "name", sortOrder: ASCENDING}]) {
              totalCount pageInfo { endCursor hasNextPage }
              items { __typename
                ... on SystemFilesFolder { rtId name children(ckTypeIds: ["System.Files/Folder", "System.Files/FileSystemItem"], first: 1) { totalCount } }
                ... on SystemFilesFileSystemItem { rtId name content { binaryId filename contentType size downloadUri }
                  associations(ckId: "System.Files/FileSystemItem", roleId: "System/RelatesTo", direction: INBOUND, first: 3) { totalCount items { rtId ckTypeId rtDisplayName } } } } } } } }
        }
        """;

    private readonly FilesTestFixture _fixture;
    private readonly FileSystemServiceTests _helpers;

    public FileSystemMutationGuardTests(FilesTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _helpers = new FileSystemServiceTests(fixture, output);
    }

    private ITenantRepository Repository => _fixture.GetSystemContext().GetTenantRepository();

    [Fact]
    public async Task CreateRoot_RequiresFileManagement_AndAFreeNonReservedWellKnownName()
    {
        var wellKnownName = "Docs" + Guid.NewGuid().ToString("N")[..6];

        var denied = await RunAsync(CreateRoot, new { name = "Docs", wellKnownName }, FilesTestFixture.PlainUser);
        ErrorCode(denied).Should().Be(FileSystemErrorCodes.Forbidden);

        var created = await RunAsync(CreateRoot, new { name = "Docs", wellKnownName }, FilesTestFixture.FileManager);
        created.Errors.Should().BeNullOrEmpty();

        var duplicate = await RunAsync(CreateRoot, new { name = "Docs 2", wellKnownName = wellKnownName.ToUpperInvariant() },
            FilesTestFixture.FileManager);
        ErrorCode(duplicate).Should().Be(FileSystemErrorCodes.NameConflict);

        var reserved = await RunAsync(CreateRoot, new { name = "Zip", wellKnownName = "zip" }, FilesTestFixture.FileManager);
        ErrorCode(reserved).Should().Be(FileSystemErrorCodes.ReservedName);
    }

    [Fact]
    public async Task ServiceRoot_Files_CannotBeRenamedOrDeleted()
    {
        using var session = Repository.GetSession();
        var files = (await _fixture.FileSystem.FindRootAsync(Repository, session, "Files"))!;

        var rename = await RunAsync(RenameRoot, new { rtId = files.Id.RtId.ToString(), name = "Other" },
            FilesTestFixture.FileManager);
        ErrorCode(rename).Should().Be(FileSystemErrorCodes.ProtectedRoot);

        var delete = await RunAsync(Delete, new { rtEntityIds = new[] { Ref(files) } }, FilesTestFixture.FileManager);
        ErrorCode(delete).Should().Be(FileSystemErrorCodes.ProtectedRoot);

        (await _fixture.FileSystem.FindRootAsync(Repository, Repository.GetSession(), "Files")).Should().NotBeNull();
    }

    [Fact]
    public async Task UserRoot_CanBeRenamed()
    {
        var root = await _helpers.CreateRootAsync();
        var rename = await RunAsync(RenameRoot, new { rtId = root.Id.RtId.ToString(), name = "Renamed" },
            FilesTestFixture.FileManager);
        rename.Errors.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CreateFolder_NamesAreUniquePerParent_CaseInsensitive()
    {
        var root = await _helpers.CreateRootAsync();

        var first = await RunAsync(CreateFolder, FolderVars("Invoices", root), FilesTestFixture.PlainUser);
        first.Errors.Should().BeNullOrEmpty();

        var second = await RunAsync(CreateFolder, FolderVars("INVOICES", root), FilesTestFixture.PlainUser);
        ErrorCode(second).Should().Be(FileSystemErrorCodes.NameConflict);

        var invalid = await RunAsync(CreateFolder, FolderVars("a/b", root), FilesTestFixture.PlainUser);
        ErrorCode(invalid).Should().Be(FileSystemErrorCodes.InvalidName);
    }

    [Fact]
    public async Task RenameFolder_ToSiblingName_IsRefused()
    {
        var root = await _helpers.CreateRootAsync();
        await _helpers.CreateFolderAsync(root, "A");
        var b = await _helpers.CreateFolderAsync(root, "B");

        var conflict = await RunAsync(RenameFolder, new { rtId = b.Id.RtId.ToString(), name = "a" }, FilesTestFixture.PlainUser);
        ErrorCode(conflict).Should().Be(FileSystemErrorCodes.NameConflict);

        var ok = await RunAsync(RenameFolder, new { rtId = b.Id.RtId.ToString(), name = "C" }, FilesTestFixture.PlainUser);
        ok.Errors.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Move_RespectsNamesAndRefusesMovingAFolderBelowItself()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        var aa = await _helpers.CreateFolderAsync(a, "AA");
        var item = await _helpers.UploadAsync(root, "x.txt", "x", FileConflictMode.Fail);
        await _helpers.UploadAsync(a, "X.TXT", "other", FileConflictMode.Fail);

        var conflict = await RunAsync(MoveItem, MoveVars(item, root, a), FilesTestFixture.PlainUser);
        ErrorCode(conflict).Should().Be(FileSystemErrorCodes.NameConflict);

        var intoItself = await RunAsync(MoveFolder, MoveVars(a, root, aa), FilesTestFixture.PlainUser);
        ErrorCode(intoItself).Should().Be(FileSystemErrorCodes.MoveIntoItself);

        var ok = await RunAsync(MoveFolder, MoveVars(aa, a, root), FilesTestFixture.PlainUser);
        ok.Errors.Should().BeNullOrEmpty();

        using var session = Repository.GetSession();
        var moved = await _fixture.FileSystem.ResolveAsync(Repository, session, root.Entity.RtWellKnownName!, "AA");
        moved.Id.Should().Be(aa.Id);
    }

    [Fact]
    public async Task DeleteFolder_CascadesToEverythingBelow_IncludingTheBytes()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        var b = await _helpers.CreateFolderAsync(a, "B");
        var deep = await _helpers.UploadAsync(b, "deep.txt", "deep", FileConflictMode.Fail);
        var keep = await _helpers.UploadAsync(root, "keep.txt", "keep", FileConflictMode.Fail);
        var binaryId = deep.Content!.BinaryId!.Value.ToString();
        (await _fixture.CountGridFsFilesAsync(binaryId)).Should().Be(1);

        var result = await RunAsync(Delete, new { rtEntityIds = new[] { Ref(a) } }, FilesTestFixture.PlainUser);
        result.Errors.Should().BeNullOrEmpty();

        using var session = Repository.GetSession();
        (await _fixture.FileSystem.FindByRtIdAsync(Repository, session, a.Id.RtId)).Should().BeNull();
        (await _fixture.FileSystem.FindByRtIdAsync(Repository, session, b.Id.RtId)).Should().BeNull();
        (await _fixture.FileSystem.FindByRtIdAsync(Repository, session, deep.Id.RtId)).Should().BeNull();
        (await _fixture.FileSystem.FindByRtIdAsync(Repository, session, keep.Id.RtId)).Should().NotBeNull();
        (await _fixture.CountGridFsFilesAsync(binaryId)).Should().Be(0, "erased files take their GridFS bytes along");
        (await _fixture.FileSystem.GetChildrenAsync(Repository, session, root.Id)).Should().ContainSingle();
    }

    /// <remarks>
    ///     The Studio's fragment asks <c>children(first: 0) { totalCount }</c>; the engine answers
    ///     <c>first: 0</c> on a nested association connection with a Mongo error ($slice needs a positive
    ///     count), so the contract uses <c>first: 1</c> until the engine accepts 0.
    /// </remarks>
    [Fact]
    public async Task DeleteFolder_WithEntriesTheCallerMayNotDelete_FailsAsAWhole()
    {
        var root = await _helpers.CreateRootAsync();
        var folder = await _helpers.CreateFolderAsync(root, "Shared");
        var alice = FilesTestFixture.CreateUser("alice", "FileUser");
        var bob = FilesTestFixture.CreateUser("bob", "FileUser");

        _fixture.Permissions.Table = OwnedFilesTable();
        try
        {
            var aliceFile = await UploadAsAsync("alice", folder, "alice.txt");

            var byBob = await RunAsync(Delete, new { rtEntityIds = new[] { Ref(folder) } }, bob);
            byBob.Errors.Should().NotBeNullOrEmpty("bob may not delete alice's file, so the folder stays");

            using (var system = Repository.GetSession())
            {
                (await _fixture.FileSystem.FindByRtIdAsync(Repository, system, folder.Id.RtId)).Should().NotBeNull();
                (await _fixture.FileSystem.FindByRtIdAsync(Repository, system, aliceFile.Id.RtId)).Should().NotBeNull();
            }

            var byAlice = await RunAsync(Delete, new { rtEntityIds = new[] { Ref(folder) } }, alice);
            byAlice.Errors.Should().BeNullOrEmpty();
            using var check = Repository.GetSession();
            (await _fixture.FileSystem.FindByRtIdAsync(Repository, check, aliceFile.Id.RtId)).Should().BeNull();
        }
        finally
        {
            _fixture.Permissions.Table = Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyTable.Empty;
        }
    }

    internal static Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyTable OwnedFilesTable() =>
        new([
            new Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyRule("files.owned",
                new HashSet<string> { FileSystemConstants.FileSystemItemCkTypeId },
                [
                    Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataAction.Read,
                    Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataAction.Write,
                    Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataAction.Delete
                ],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "FileUser" })
        ]);

    private async Task<FileSystemEntry> UploadAsAsync(string subject, FileSystemEntry parent, string name)
    {
        using var session = Repository.GetSession(
            Meshmakers.Octo.Runtime.Contracts.RtSecurityContext.ForUser(subject, ["FileUser"]));
        using var unfiltered = Repository.GetSession();
        session.StartTransaction();
        var (entry, _) = await _fixture.FileSystem.UploadAsync(Repository, session, unfiltered, parent, name,
            "text/plain", new MemoryStream("x"u8.ToArray()), FileConflictMode.Fail);
        await session.CommitTransactionAsync();
        return entry;
    }

    [Fact]
    public async Task Move_WithoutRemovingTheCurrentParent_IsRefused()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        var item = await _helpers.UploadAsync(root, "x.txt", "x", FileConflictMode.Fail);

        const string addParentOnly = """
            mutation ($rtId: OctoObjectId!, $toCkTypeId: RtCkTypeId!, $toRtId: OctoObjectId!) {
              runtime { systemFilesFileSystemItems { update(entities: [{rtId: $rtId, item: {parent: [
                {modOption: CREATE, target: {ckTypeId: $toCkTypeId, rtId: $toRtId}}]}}]) { rtId } } }
            }
            """;
        var result = await RunAsync(addParentOnly, new
        {
            rtId = item.Id.RtId.ToString(),
            toCkTypeId = FileSystemService.SemanticName(a.Id.CkTypeId),
            toRtId = a.Id.RtId.ToString()
        }, FilesTestFixture.PlainUser);
        ErrorCode(result).Should().Be(FileSystemErrorCodes.InvalidRequest);

        using var session = Repository.GetSession();
        (await _fixture.FileSystem.GetParentIdsAsync(Repository, session, item.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFolder_WithoutParent_IsRefused()
    {
        const string orphan = """
            mutation { runtime { systemFilesFolders { create(entities: [{name: "Orphan"}]) { rtId } } } }
            """;
        var result = await RunAsync(orphan, new { }, FilesTestFixture.PlainUser);
        ErrorCode(result).Should().Be(FileSystemErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task CreateRoot_WithTheReportingPrefix_IsReserved()
    {
        var result = await RunAsync(CreateRoot, new { name = "Fake", wellKnownName = "ReportingAssets_Fake" },
            FilesTestFixture.FileManager);
        ErrorCode(result).Should().Be(FileSystemErrorCodes.ReservedName);
    }

    [Fact]
    public async Task Delete_AboveTheCascadeLimit_IsRefusedBeforeAnythingIsDeleted()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        await _helpers.UploadAsync(a, "1.txt", "1", FileConflictMode.Fail);
        await _helpers.UploadAsync(a, "2.txt", "2", FileConflictMode.Fail);

        var guard = new FileSystemMutationGuard(_fixture.FileSystem,
            Microsoft.Extensions.Options.Options.Create(new FilesOptions { MaxDeleteEntries = 2 }));
        var expand = async () => await guard.ExpandDeleteAsync(Repository, [a.Id], TestContext.Current.CancellationToken);
        (await expand.Should().ThrowAsync<FileSystemException>()).Which.Code.Should().Be(FileSystemErrorCodes.LimitExceeded);
    }

    [Fact]
    public void QueryRowMutations_CannotWriteFiles()
    {
        var act = () => FileSystemMutationGuard.EnsureNotInQueryMutation(
            [new Meshmakers.Octo.ConstructionKit.Contracts.RtCkId<Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId>(
                FileSystemConstants.FolderCkTypeId)], []);
        act.Should().Throw<FileSystemException>().Which.Code.Should().Be(FileSystemErrorCodes.InvalidRequest);

        var other = () => FileSystemMutationGuard.EnsureNotInQueryMutation(
            [new Meshmakers.Octo.ConstructionKit.Contracts.RtCkId<Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId>(
                "AssetRepositoryIntegrationTest/Product")], []);
        other.Should().NotThrow();
    }

    [Fact]
    public async Task StudioChildrenQuery_MatchesTheSchema()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        await _helpers.CreateFolderAsync(a, "Sub");
        await _helpers.UploadAsync(a, "f.txt", "f", FileConflictMode.Fail);

        var result = await RunAsync(FolderChildren, new
        {
            rtId = a.Id.RtId.ToString(),
            ckTypeIds = new[] { "System.Files/Folder", "System.Files/FileSystemItem" },
            first = 50
        }, FilesTestFixture.PlainUser);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        var json = JObject.Parse(_fixture.SerializeGraphQl(result));
        var children = json.SelectToken("data.runtime.systemFilesFolder.items[0].children")!;
        children["totalCount"]!.Value<int>().Should().Be(2);
        children["items"]!.Select(i => i["__typename"]!.Value<string>())
            .Should().BeEquivalentTo("SystemFilesFolder", "SystemFilesFileSystemItem");
    }

    // ---------------------------------------------------------------------------------------------

    private static object Ref(FileSystemEntry entry) => new { ckTypeId = FileSystemService.SemanticName(entry.Id.CkTypeId), rtId = entry.Id.RtId.ToString() };

    private static object FolderVars(string name, FileSystemEntry parent) => new
    {
        name,
        parentCkTypeId = FileSystemService.SemanticName(parent.Id.CkTypeId),
        parentRtId = parent.Id.RtId.ToString()
    };

    private static object MoveVars(FileSystemEntry entry, FileSystemEntry from, FileSystemEntry to) => new
    {
        rtId = entry.Id.RtId.ToString(),
        fromCkTypeId = FileSystemService.SemanticName(from.Id.CkTypeId),
        fromRtId = from.Id.RtId.ToString(),
        toCkTypeId = FileSystemService.SemanticName(to.Id.CkTypeId),
        toRtId = to.Id.RtId.ToString()
    };

    private async Task<ExecutionResult> RunAsync(string document, object variables, ClaimsPrincipal user)
    {
        var result = await _fixture.ExecuteGraphQlAsync(document, JsonSerializer.Serialize(variables), user);
        _fixture.OutputHelper?.WriteLine(_fixture.SerializeGraphQl(result));
        return result;
    }

    private static string? ErrorCode(ExecutionResult result) => result.Errors?.FirstOrDefault()?.Code;
}
