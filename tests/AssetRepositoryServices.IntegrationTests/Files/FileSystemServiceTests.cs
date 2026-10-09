using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6171 S2: default root seeding and the tree operations of <see cref="FileSystemService" />.
/// </summary>
[Collection(FilesCollection.Name)]
public class FileSystemServiceTests
{
    private readonly FilesTestFixture _fixture;

    public FileSystemServiceTests(FilesTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private ITenantRepository Repository => _fixture.GetSystemContext().GetTenantRepository();

    [Fact]
    public async Task DefaultRoot_IsSeededOnce_EvenWhenMigrationsRunAgain()
    {
        await _fixture.RunServiceMigrationsAsync();

        using var session = Repository.GetSession();
        var roots = await _fixture.FileSystem.GetRootsAsync(Repository, session);
        roots.Where(r => r.Entity.RtWellKnownName == "Files").Should().ContainSingle()
            .Which.Name.Should().Be("Files");
    }

    [Fact]
    public async Task DefaultRoot_WithoutSystemFiles_DoesNotThrow()
    {
        // The test tenant has no System.Files: the tenant start must log and go on, not fail the tenant.
        var tenant = await _fixture.GetTestTenantContextAsync();
        var ensured = await _fixture.GetService<FileSystemDefaults>().EnsureDefaultRootAsync(tenant);
        ensured.Should().BeFalse();
    }

    [Fact]
    public async Task Upload_ThenResolveByPath_FindsTheFileWithItsContent()
    {
        var root = await CreateRootAsync();
        var folder = await CreateFolderAsync(root, "Receipts 2026");

        using var session = Repository.GetSession();
        session.StartTransaction();
        var (item, replaced) = await _fixture.FileSystem.UploadAsync(Repository, session, session, folder,
            "Rechnung Ä.pdf", "application/pdf", new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 test")),
            FileConflictMode.Fail);
        await session.CommitTransactionAsync();

        replaced.Should().BeFalse();
        item.Kind.Should().Be(FileSystemEntryKind.File);
        item.Content!.BinaryId.Should().NotBeNull();
        item.Content.Size.Should().Be(13);

        using var readSession = Repository.GetSession();
        var resolved = await _fixture.FileSystem.ResolveAsync(Repository, readSession,
            root.Entity.RtWellKnownName!, "Receipts 2026/Rechnung Ä.pdf");
        resolved.Id.Should().Be(item.Id);

        var caseInsensitive = await _fixture.FileSystem.ResolveAsync(Repository, readSession,
            root.Entity.RtWellKnownName!.ToUpperInvariant(), "receipts 2026/rechnung ä.pdf");
        caseInsensitive.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task Upload_ConflictModes_FailReplaceKeepBoth()
    {
        var root = await CreateRootAsync();

        var first = await UploadAsync(root, "a.txt", "one", FileConflictMode.Fail);

        var fail = async () => await UploadAsync(root, "A.TXT", "two", FileConflictMode.Fail);
        (await fail.Should().ThrowAsync<FileSystemException>()).Which.Code.Should().Be(FileSystemErrorCodes.NameConflict);

        var replaced = await UploadAsync(root, "a.txt", "three!", FileConflictMode.Replace);
        replaced.Id.Should().Be(first.Id, "a replace keeps the entity (rtId and links)");
        replaced.Content!.Size.Should().Be(6);
        replaced.Content.BinaryId.Should().NotBe(first.Content!.BinaryId);

        var kept = await UploadAsync(root, "a.txt", "four", FileConflictMode.KeepBoth);
        kept.Name.Should().Be("a (1).txt");
        var kept2 = await UploadAsync(root, "a.txt", "five", FileConflictMode.KeepBoth);
        kept2.Name.Should().Be("a (2).txt");
    }

    [Fact]
    public async Task Resolve_UnknownRootOrPath_AnswersStableCodes()
    {
        var root = await CreateRootAsync();
        using var session = Repository.GetSession();

        var noRoot = async () => await _fixture.FileSystem.ResolveAsync(Repository, session, "does-not-exist", "x");
        (await noRoot.Should().ThrowAsync<FileSystemException>()).Which.Code.Should().Be(FileSystemErrorCodes.RootNotFound);

        var noPath = async () => await _fixture.FileSystem.ResolveAsync(Repository, session,
            root.Entity.RtWellKnownName!, "missing/file.pdf");
        (await noPath.Should().ThrowAsync<FileSystemException>()).Which.Code.Should().Be(FileSystemErrorCodes.PathNotFound);
    }

    [Fact]
    public async Task Walk_ReturnsAllDescendantsWithRelativePaths()
    {
        var root = await CreateRootAsync();
        var a = await CreateFolderAsync(root, "A");
        var b = await CreateFolderAsync(a, "B");
        await UploadAsync(root, "top.txt", "t", FileConflictMode.Fail);
        await UploadAsync(b, "deep.txt", "d", FileConflictMode.Fail);

        using var session = Repository.GetSession();
        var walk = await _fixture.FileSystem.WalkAsync(Repository, session, root, cancellationToken: TestContext.Current.CancellationToken);

        walk.Truncated.Should().BeFalse();
        walk.Items.Select(i => i.RelativePath).Should().BeEquivalentTo("A", "top.txt", "A/B", "A/B/deep.txt");

        var limited = await _fixture.FileSystem.WalkAsync(Repository, session, root, 2, TestContext.Current.CancellationToken);
        limited.Truncated.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------

    internal async Task<FileSystemEntry> CreateRootAsync(string? wellKnownName = null)
    {
        wellKnownName ??= "Root" + Guid.NewGuid().ToString("N")[..8];
        var root = await Repository.CreateTransientRtEntityAsync<RtFolderRoot>();
        root.Name = wellKnownName;
        root.RtWellKnownName = wellKnownName;
        using var session = Repository.GetSession();
        session.StartTransaction();
        await Repository.InsertOneRtEntityAsync(session, root);
        await session.CommitTransactionAsync();
        return (await _fixture.FileSystem.FindRootAsync(Repository, Repository.GetSession(), wellKnownName))!;
    }

    internal async Task<FileSystemEntry> CreateFolderAsync(FileSystemEntry parent, string name)
    {
        var folder = await Repository.CreateTransientRtEntityAsync<RtFolder>();
        folder.Name = name;
        using var session = Repository.GetSession();
        session.StartTransaction();
        var result = new OperationResult();
        await Repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(FileSystemService.FolderType, folder)],
            [AssociationUpdateInfo.CreateInsert(new RtEntityId(FileSystemService.FolderType, folder.RtId), parent.Id,
                FileSystemService.ParentChildRole)], result);
        await session.CommitTransactionAsync();
        result.HasErrors.Should().BeFalse(result.GetMessages());
        return (await _fixture.FileSystem.FindByRtIdAsync(Repository, Repository.GetSession(), folder.RtId))!;
    }

    internal async Task<FileSystemEntry> UploadAsync(FileSystemEntry parent, string name, string content,
        FileConflictMode mode)
    {
        using var session = Repository.GetSession();
        session.StartTransaction();
        var (entry, _) = await _fixture.FileSystem.UploadAsync(Repository, session, session, parent, name,
            "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(content)), mode);
        await session.CommitTransactionAsync();
        return entry;
    }
}
