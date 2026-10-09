using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6171 S2b/S2c: the REST bytes API (<see cref="FilesController" />) and the download headers of
///     <see cref="LargeBinariesController" />, called directly against the test tenant.
/// </summary>
[Collection(FilesCollection.Name)]
public class FilesControllerTests
{
    private readonly FilesTestFixture _fixture;
    private readonly FileSystemServiceTests _helpers;

    public FilesControllerTests(FilesTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _helpers = new FileSystemServiceTests(fixture, output);
    }

    private ITenantRepository Repository => _fixture.GetSystemContext().GetTenantRepository();

    [Fact]
    public async Task Capabilities_AdvertiseTheLimits()
    {
        var (controller, _) = Create(FilesTestFixture.PlainUser);
        var result = controller.GetCapabilities().Should().BeOfType<OkObjectResult>().Subject;
        var caps = result.Value.Should().BeOfType<FilesCapabilitiesDto>().Subject;
        caps.MaxUploadBytes.Should().Be(100L * 1024 * 1024);
        caps.ZipMaxFiles.Should().Be(1000);
        caps.ZipMaxBytes.Should().Be(500L * 1024 * 1024);
        caps.PreviewMaxBytes.Should().Be(20L * 1024 * 1024);
        caps.ReservedRootNames.Should().Contain(["capabilities", "zip", "items", "stats"]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UploadByPath_ThenDownloadByPath_KeepsBytesAndRealFileName()
    {
        var root = await _helpers.CreateRootAsync();
        await _helpers.CreateFolderAsync(root, "Belege");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 Rechnung");

        var (upload, _) = Create(FilesTestFixture.PlainUser, bytes, "application/pdf");
        var created = await upload.UploadByPath(root.Entity.RtWellKnownName!, "Belege/Rechnung Ä.pdf");
        var dto = created.Should().BeOfType<ObjectResult>().Subject;
        dto.StatusCode.Should().Be(StatusCodes.Status201Created);
        var entry = dto.Value.Should().BeOfType<FileEntryDto>().Subject;
        entry.Name.Should().Be("Rechnung Ä.pdf");
        entry.Path.Should().Be("Belege/Rechnung Ä.pdf");
        entry.Size.Should().Be(bytes.Length);
        entry.ContentType.Should().Be("application/pdf");

        var (download, http) = Create(FilesTestFixture.PlainUser);
        var file = (await download.DownloadByPath(root.Entity.RtWellKnownName!, "Belege/Rechnung Ä.pdf"))
            .Should().BeOfType<FileStreamResult>().Subject;
        (await ReadAllAsync(file.FileStream)).Should().Equal(bytes);
        file.ContentType.Should().Be("application/pdf");
        var disposition = http.Response.Headers.ContentDisposition.ToString();
        disposition.Should().StartWith("attachment;");
        disposition.Should().Contain("filename=\"Rechnung A.pdf\"");
        disposition.Should().Contain("filename*=UTF-8''Rechnung%20%C3%84.pdf");
        http.Response.Headers.AccessControlExposeHeaders.ToString().Should().Contain("Content-Disposition");
        http.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task UploadByPath_ConflictModes()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;

        (await Upload(wkn, "x.txt", "one")).StatusCode.Should().Be(StatusCodes.Status201Created);

        var conflict = await Upload(wkn, "X.txt", "two");
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        Code(conflict).Should().Be(FileSystemErrorCodes.NameConflict);

        var replaced = await Upload(wkn, "x.txt", "three", "replace");
        replaced.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((FileEntryDto)replaced.Value!).Replaced.Should().BeTrue();

        var kept = await Upload(wkn, "x.txt", "four", "keepBoth");
        ((FileEntryDto)kept.Value!).Name.Should().Be("x (1).txt");

        var badMode = await Upload(wkn, "y.txt", "five", "overwrite");
        badMode.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Upload_AboveTheLimit_Answers413_WithAndWithoutContentLength()
    {
        var root = await _helpers.CreateRootAsync();
        var limits = new FilesOptions { MaxUploadBytes = 10 };

        var (declared, _) = Create(FilesTestFixture.PlainUser, new byte[11], "text/plain", limits);
        var r1 = (ObjectResult)await declared.UploadByPath(root.Entity.RtWellKnownName!, "big.txt");
        r1.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Code(r1).Should().Be(FileSystemErrorCodes.LimitExceeded);

        var (chunked, http) = Create(FilesTestFixture.PlainUser, new byte[11], "text/plain", limits);
        http.Request.ContentLength = null;
        var r2 = (ObjectResult)await chunked.UploadByPath(root.Entity.RtWellKnownName!, "big.txt");
        r2.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);

        using var session = Repository.GetSession();
        (await _fixture.FileSystem.GetChildrenAsync(Repository, session, root.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task UploadByPath_MissingFolders_404OrCreatedOnRequest()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;

        var (c1, _) = Create(FilesTestFixture.PlainUser, "a"u8.ToArray(), "text/plain");
        var missing = (ObjectResult)await c1.UploadByPath(wkn, "2026/Q4/a.txt");
        missing.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Code(missing).Should().Be(FileSystemErrorCodes.PathNotFound);

        var (c2, _) = Create(FilesTestFixture.PlainUser, "a"u8.ToArray(), "text/plain");
        var created = (ObjectResult)await c2.UploadByPath(wkn, "2026/Q4/a.txt", createFolders: true);
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        using var session = Repository.GetSession();
        (await _fixture.FileSystem.ResolveAsync(Repository, session, wkn, "2026/Q4/a.txt")).Kind
            .Should().Be(FileSystemEntryKind.File);
    }

    [Fact]
    public async Task ByRtId_UploadIntoFolder_Download_Replace()
    {
        var root = await _helpers.CreateRootAsync();
        var folder = await _helpers.CreateFolderAsync(root, "F");

        var (c1, _) = Create(FilesTestFixture.PlainUser, "first"u8.ToArray(), "text/plain");
        var created = (ObjectResult)await c1.UploadIntoFolderById(folder.Id.RtId.ToString(), "doc.txt");
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var rtId = ((FileEntryDto)created.Value!).RtId;

        var (c2, _) = Create(FilesTestFixture.PlainUser, "second!"u8.ToArray(), "text/plain");
        var replaced = (ObjectResult)await c2.ReplaceById(rtId);
        replaced.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((FileEntryDto)replaced.Value!).RtId.Should().Be(rtId);

        var (c3, _) = Create(FilesTestFixture.PlainUser);
        var file = (FileStreamResult)await c3.DownloadById(rtId);
        Encoding.UTF8.GetString(await ReadAllAsync(file.FileStream)).Should().Be("second!");

        var (c4, _) = Create(FilesTestFixture.PlainUser);
        var notAFile = (ObjectResult)await c4.DownloadById(folder.Id.RtId.ToString());
        Code(notAFile).Should().Be(FileSystemErrorCodes.NotAFile);
    }

    [Fact]
    public async Task Download_ActiveContentIsNeverInline_PassiveContentMayBe()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        await Upload(wkn, "evil.svg", "<svg onload=\"alert(1)\"/>", contentType: "image/svg+xml");
        await Upload(wkn, "doc.pdf", "%PDF-1.4", contentType: "application/pdf");

        var (c1, svgHttp) = Create(FilesTestFixture.PlainUser);
        await c1.DownloadByPath(wkn, "evil.svg", inline: true);
        svgHttp.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment;");
        svgHttp.Response.Headers.ContentSecurityPolicy.ToString().Should().StartWith("sandbox");

        var (c2, pdfHttp) = Create(FilesTestFixture.PlainUser);
        await c2.DownloadByPath(wkn, "doc.pdf", inline: true);
        pdfHttp.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline;");
        pdfHttp.Response.Headers.ContentSecurityPolicy.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task DataPermissions_HiddenFilesAre404_AndCountAsHiddenInStats()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var folder = await _helpers.CreateFolderAsync(root, "Shared");
        var alice = FilesTestFixture.CreateUser("alice", "FileUser");
        var bob = FilesTestFixture.CreateUser("bob", "FileUser");

        _fixture.Permissions.Table = FileSystemMutationGuardTests.OwnedFilesTable();
        try
        {
            var (a1, _) = Create(alice, "secret"u8.ToArray(), "text/plain");
            ((ObjectResult)await a1.UploadByPath(wkn, "Shared/alice.txt")).StatusCode.Should().Be(201);
            var (b0, _) = Create(bob, "mine"u8.ToArray(), "text/plain");
            ((ObjectResult)await b0.UploadByPath(wkn, "Shared/bob.txt")).StatusCode.Should().Be(201);

            var (b1, _) = Create(bob);
            var hidden = (ObjectResult)await b1.DownloadByPath(wkn, "Shared/alice.txt");
            hidden.StatusCode.Should().Be(StatusCodes.Status404NotFound);

            var (b2, _) = Create(bob, "x"u8.ToArray(), "text/plain");
            var nameTaken = (ObjectResult)await b2.UploadByPath(wkn, "Shared/alice.txt");
            nameTaken.StatusCode.Should().Be(StatusCodes.Status409Conflict, "hidden entries still block their name");

            var (b3, _) = Create(bob);
            var stats = (FolderStatsDto)((OkObjectResult)await b3.GetStatsById(folder.Id.RtId.ToString())).Value!;
            stats.Files.Should().Be(1);
            stats.HiddenEntries.Should().Be(1);
            stats.Complete.Should().BeTrue();

            var (a2, _) = Create(alice);
            var aliceStats = (FolderStatsDto)((OkObjectResult)await a2.GetStatsByPath(wkn, "Shared")).Value!;
            aliceStats.Files.Should().Be(1);
            aliceStats.Bytes.Should().Be(6);
        }
        finally
        {
            _fixture.Permissions.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task Stats_CountTheWholeTree()
    {
        var root = await _helpers.CreateRootAsync();
        var a = await _helpers.CreateFolderAsync(root, "A");
        await _helpers.CreateFolderAsync(a, "B");
        await _helpers.UploadAsync(root, "1.txt", "12", FileConflictMode.Fail);
        await _helpers.UploadAsync(a, "2.txt", "345", FileConflictMode.Fail);

        var (controller, _) = Create(FilesTestFixture.PlainUser);
        var stats = (FolderStatsDto)((OkObjectResult)await controller.GetStatsByPath(root.Entity.RtWellKnownName!, null)).Value!;
        stats.Folders.Should().Be(2);
        stats.Files.Should().Be(2);
        stats.Bytes.Should().Be(5);
        stats.HiddenEntries.Should().Be(0);
        stats.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task LargeBinaries_SendTheStoredFileName()
    {
        var root = await _helpers.CreateRootAsync();
        var item = await _helpers.UploadAsync(root, "Übersicht.txt", "hello", FileConflictMode.Fail);

        var http = NewHttpContext(FilesTestFixture.PlainUser);
        var controller = new LargeBinariesController(_fixture.GetService<IOctoService>(),
            _fixture.GetService<IDataPermissionResolver>(),
            _fixture.GetService<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Get(item.Content!.BinaryId!.Value.ToString());
        result.Should().BeOfType<FileStreamResult>();
        var disposition = http.Response.Headers.ContentDisposition.ToString();
        disposition.Should().StartWith("inline;", "the default stays inline for passive content");
        disposition.Should().Contain("filename*=UTF-8''%C3%9Cbersicht.txt");
        http.Response.Headers.AccessControlExposeHeaders.ToString().Should().Contain("Content-Disposition");
    }

    [Fact]
    public async Task Zip_FoldersAndFiles_ByPathAndRtId_StreamsAValidArchive()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var a = await _helpers.CreateFolderAsync(root, "A");
        await _helpers.CreateFolderAsync(a, "Empty");
        await _helpers.UploadAsync(a, "in-a.txt", "aaa", FileConflictMode.Fail);
        var top = await _helpers.UploadAsync(root, "top.txt", "top", FileConflictMode.Fail);
        var other = await _helpers.CreateFolderAsync(root, "B");
        await _helpers.UploadAsync(other, "top.txt", "other top", FileConflictMode.Fail);

        var (controller, http) = Create(FilesTestFixture.PlainUser);
        var body = new MemoryStream();
        http.Response.Body = body;
        var result = await controller.DownloadZip(new ZipRequestDto
        {
            Items = [new FileRefDto { Root = wkn, Path = "A" }, new FileRefDto { Root = wkn, Path = "B/top.txt" }],
            RtIds = [top.Id.RtId.ToString()],
            FileName = "Auswahl Ä"
        });

        result.Should().BeOfType<EmptyResult>();
        http.Response.ContentType.Should().Be("application/zip");
        http.Response.Headers.ContentDisposition.ToString().Should().Contain("filename*=UTF-8''Auswahl%20%C3%84.zip");

        body.Position = 0;
        using var zip = new global::System.IO.Compression.ZipArchive(body, global::System.IO.Compression.ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            "A/", "A/Empty/", "A/in-a.txt", "top.txt", "top (1).txt");
        using var reader = new StreamReader(zip.GetEntry("A/in-a.txt")!.Open());
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Should().Be("aaa");
    }

    [Fact]
    public async Task Zip_Limits_AreCheckedBeforeTheFirstByte()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        await _helpers.UploadAsync(root, "1.txt", "11111", FileConflictMode.Fail);
        await _helpers.UploadAsync(root, "2.txt", "22222", FileConflictMode.Fail);

        var (tooMany, http1) = Create(FilesTestFixture.PlainUser, limits: new FilesOptions { ZipMaxFiles = 1 });
        var r1 = (ObjectResult)await tooMany.DownloadZip(new ZipRequestDto { Items = [new FileRefDto { Root = wkn }] });
        r1.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Code(r1).Should().Be(FileSystemErrorCodes.LimitExceeded);
        http1.Response.HasStarted.Should().BeFalse();

        var (tooBig, _) = Create(FilesTestFixture.PlainUser, limits: new FilesOptions { ZipMaxBytes = 9 });
        var r2 = (ObjectResult)await tooBig.DownloadZip(new ZipRequestDto { Items = [new FileRefDto { Root = wkn }] });
        r2.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);

        var (missing, _) = Create(FilesTestFixture.PlainUser);
        var r3 = (ObjectResult)await missing.DownloadZip(new ZipRequestDto { Items = [new FileRefDto { Root = wkn, Path = "nope.txt" }] });
        r3.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var (empty, _) = Create(FilesTestFixture.PlainUser);
        var r4 = (ObjectResult)await empty.DownloadZip(new ZipRequestDto());
        r4.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Zip_LeavesOutFilesHiddenByDataPermissions()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        await _helpers.CreateFolderAsync(root, "Shared");
        var alice = FilesTestFixture.CreateUser("alice", "FileUser");
        var bob = FilesTestFixture.CreateUser("bob", "FileUser");

        _fixture.Permissions.Table = FileSystemMutationGuardTests.OwnedFilesTable();
        try
        {
            var (a1, _) = Create(alice, "secret"u8.ToArray(), "text/plain");
            await a1.UploadByPath(wkn, "Shared/alice.txt");
            var (b1, _) = Create(bob, "mine"u8.ToArray(), "text/plain");
            await b1.UploadByPath(wkn, "Shared/bob.txt");

            var (zipper, http) = Create(bob);
            var body = new MemoryStream();
            http.Response.Body = body;
            await zipper.DownloadZip(new ZipRequestDto { Items = [new FileRefDto { Root = wkn, Path = "Shared" }] });
            body.Position = 0;
            using var zip = new global::System.IO.Compression.ZipArchive(body, global::System.IO.Compression.ZipArchiveMode.Read);
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("Shared/", "Shared/bob.txt");
        }
        finally
        {
            _fixture.Permissions.Table = RtDataPolicyTable.Empty;
        }
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<ObjectResult> Upload(string root, string path, string content, string? conflict = null,
        string contentType = "text/plain")
    {
        var (controller, _) = Create(FilesTestFixture.PlainUser, Encoding.UTF8.GetBytes(content), contentType);
        return (ObjectResult)await controller.UploadByPath(root, path, conflict);
    }

    private (FilesController Controller, DefaultHttpContext Http) Create(ClaimsPrincipal user, byte[]? body = null,
        string? contentType = null, FilesOptions? limits = null)
    {
        var http = NewHttpContext(user);
        if (body != null)
        {
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = body.Length;
            http.Request.ContentType = contentType;
        }

        var controller = new FilesController(_fixture.GetService<IOctoService>(), _fixture.FileSystem,
            _fixture.GetService<FileZipService>(), Options.Create(limits ?? new FilesOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, http);
    }

    private DefaultHttpContext NewHttpContext(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user, RequestServices = _fixture.Provider! };
        http.Request.RouteValues["tenantId"] = _fixture.GetSystemContext().TenantId;
        return http;
    }

    private static string? Code(ObjectResult result) =>
        (result.Value as ProblemDetails)?.Extensions["code"] as string;

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
