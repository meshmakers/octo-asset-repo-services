using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6467: the file roles gate the Files API on every path. FileViewer reads, FileManagement reads and
///     writes, a caller with the API scope but no file role gets nothing — REST answers 403 FORBIDDEN, GraphQL
///     reads return nothing and GraphQL mutations fail with FORBIDDEN. The tests run the controller and the
///     GraphQL mutations with the built-in baseline (<see cref="FileSystemPermissionBaseline" />) and, as a second
///     tenant shape, with the data policies the System.Identity.Bootstrap blueprint seeds.
/// </summary>
[Collection(FilesCollection.Name)]
public class FilesRoleGateTests : IAsyncLifetime
{
    private const string CreateRoot = """
        mutation ($name: String!, $wellKnownName: String!) {
          runtime { systemFilesFolderRoots { create(entities: [{name: $name, rtWellKnownName: $wellKnownName}]) { rtId } } }
        }
        """;

    private const string CreateFolder = """
        mutation ($name: String!, $parentCkTypeId: RtCkTypeId!, $parentRtId: OctoObjectId!) {
          runtime { systemFilesFolders { create(entities: [{name: $name,
            parent: [{modOption: CREATE, target: {ckTypeId: $parentCkTypeId, rtId: $parentRtId}}]}]) { rtId } } }
        }
        """;

    private const string RenameItem = """
        mutation ($rtId: OctoObjectId!, $name: String!) {
          runtime { systemFilesFileSystemItems { update(entities: [{rtId: $rtId, item: {name: $name}}]) { rtId } } }
        }
        """;

    private const string RenameFolder = """
        mutation ($rtId: OctoObjectId!, $name: String!) {
          runtime { systemFilesFolders { update(entities: [{rtId: $rtId, item: {name: $name}}]) { rtId } } }
        }
        """;

    private const string MoveItem = """
        mutation ($rtId: OctoObjectId!, $fromCkTypeId: RtCkTypeId!, $fromRtId: OctoObjectId!, $toCkTypeId: RtCkTypeId!, $toRtId: OctoObjectId!) {
          runtime { systemFilesFileSystemItems { update(entities: [{rtId: $rtId, item: {parent: [
            {modOption: DELETE, target: {ckTypeId: $fromCkTypeId, rtId: $fromRtId}},
            {modOption: CREATE, target: {ckTypeId: $toCkTypeId, rtId: $toRtId}}]}}]) { rtId } } }
        }
        """;

    private const string MoveFolder = """
        mutation ($rtId: OctoObjectId!, $fromCkTypeId: RtCkTypeId!, $fromRtId: OctoObjectId!, $toCkTypeId: RtCkTypeId!, $toRtId: OctoObjectId!) {
          runtime { systemFilesFolders { update(entities: [{rtId: $rtId, item: {parent: [
            {modOption: DELETE, target: {ckTypeId: $fromCkTypeId, rtId: $fromRtId}},
            {modOption: CREATE, target: {ckTypeId: $toCkTypeId, rtId: $toRtId}}]}}]) { rtId } } }
        }
        """;

    private const string Delete = """
        mutation ($rtEntityIds: [RtEntityId]!) {
          runtime { runtimeEntities { delete(entities: $rtEntityIds) } }
        }
        """;

    private const string FolderChildren = """
        query ($rtId: OctoObjectId!) {
          runtime { systemFilesFolder(rtId: $rtId) { items { rtId
            children(ckTypeIds: ["System.Files/Folder", "System.Files/FileSystemItem"], first: 50) {
              totalCount items { __typename ... on SystemFilesFileSystemItem { name } ... on SystemFilesFolder { name } } } } } }
        }
        """;

    private static readonly ClaimsPrincipal Viewer = FilesTestFixture.CreateUser("viewer", "FileViewer");
    private static readonly ClaimsPrincipal Manager = FilesTestFixture.CreateUser("manager", "FileManagement");
    private static readonly ClaimsPrincipal NoFileRole = FilesTestFixture.CreateUser("nobody", "ReportingManagement");

    private readonly FilesTestFixture _fixture;
    private readonly FileSystemServiceTests _helpers;

    public FilesRoleGateTests(FilesTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _helpers = new FileSystemServiceTests(fixture, output);
    }

    private ITenantRepository Repository => _fixture.GetSystemContext().GetTenantRepository();

    public ValueTask InitializeAsync()
    {
        _fixture.Permissions.Baseline = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _fixture.Permissions.Baseline = false;
        _fixture.Permissions.Table = RtDataPolicyTable.Empty;
        return ValueTask.CompletedTask;
    }

    // ---- REST reads --------------------------------------------------------------------------------

    [Fact]
    public async Task RestReads_FileViewerAndManagerMayRead_NoFileRoleGets403()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var folder = await _helpers.CreateFolderAsync(root, "A");
        var file = await _helpers.UploadAsync(folder, "a.txt", "abc", FileConflictMode.Fail);
        var id = file.Id.RtId.ToString();

        foreach (var user in new[] { Viewer, Manager })
        {
            (await Download(user, wkn, "A/a.txt")).Should().BeOfType<FileStreamResult>();
            (await DownloadById(user, id)).Should().BeOfType<FileStreamResult>();
            (await Stats(user, wkn, "A")).Should().BeOfType<OkObjectResult>();
            (await StatsById(user, folder.Id.RtId.ToString())).Should().BeOfType<OkObjectResult>();
            (await LinkedCounts(user, id)).Should().BeOfType<OkObjectResult>();
            (await Zip(user, wkn, "A")).Should().BeOfType<EmptyResult>();
        }

        foreach (var result in new[]
                 {
                     await Download(NoFileRole, wkn, "A/a.txt"), await DownloadById(NoFileRole, id),
                     await Stats(NoFileRole, wkn, "A"), await StatsById(NoFileRole, folder.Id.RtId.ToString()),
                     await LinkedCounts(NoFileRole, id), await Zip(NoFileRole, wkn, "A")
                 })
        {
            AssertForbidden(result);
        }
    }

    [Fact]
    public async Task Stats_ForFileViewer_IsNeverA500_EvenWithLinkedFilesAndIdentityPolicies()
    {
        // The side finding of AB#6467: stats answered 500 for a FileViewer on test-2, where the tenant carried
        // the identity.administration policy next to the files. Stats walk the tree and the links of every file.
        var root = await _helpers.CreateRootAsync();
        var linked = await _helpers.UploadAsync(root, "linked.txt", "x", FileConflictMode.Fail);
        var plain = await _helpers.UploadAsync(root, "plain.txt", "y", FileConflictMode.Fail);
        using (var session = Repository.GetSession())
        {
            session.StartTransaction();
            var op = new Meshmakers.Octo.ConstructionKit.Contracts.OperationResult();
            await Repository.ApplyChangesAsync(session,
                [AssociationUpdateInfo.CreateInsert(plain.Id, linked.Id,
                    new Meshmakers.Octo.ConstructionKit.Contracts.RtCkId<Meshmakers.Octo.ConstructionKit.Contracts.CkAssociationRoleId>("System/Related"))],
                op);
            await session.CommitTransactionAsync();
            op.HasErrors.Should().BeFalse(op.GetMessages());
        }

        foreach (var auditOnly in new[] { false, true })
        {
            _fixture.Permissions.Table = IdentityAdministrationOnlyTable(auditOnly);
            var result = await Stats(Viewer, root.Entity.RtWellKnownName!, null);
            var stats = (FolderStatsDto)result.Should().BeOfType<OkObjectResult>().Subject.Value!;
            stats.Files.Should().Be(2);
            stats.HiddenEntries.Should().Be(0);
        }
    }

    [Fact]
    public async Task Stats_WithoutRoot_Is400_NotA500()
    {
        foreach (var root in new[] { null, "", "  " })
        {
            var result = (ObjectResult)await Create(Viewer).Controller.GetStatsByPath(root!, null);
            result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            ((ProblemDetails)result.Value!).Extensions["code"].Should().Be(FileSystemErrorCodes.InvalidRequest);
        }
    }

    // ---- REST writes -------------------------------------------------------------------------------

    [Fact]
    public async Task RestWrites_OnlyFileManagementMayWrite_AndADeniedWriteChangesNothing()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var folder = await _helpers.CreateFolderAsync(root, "Docs");
        var existing = await _helpers.UploadAsync(folder, "keep.txt", "original", FileConflictMode.Fail);
        var existingId = existing.Id.RtId.ToString();

        foreach (var user in new[] { Viewer, NoFileRole })
        {
            AssertForbidden(await Upload(user, wkn, "Docs/new.txt", "x"));
            AssertForbidden(await Upload(user, wkn, "Docs/new2/deep.txt", "x", createFolders: true));
            AssertForbidden(await Upload(user, wkn, "Docs/keep.txt", "overwritten", conflict: "replace"));
            AssertForbidden(await UploadIntoFolder(user, folder.Id.RtId.ToString(), "byid.txt", "x"));
            AssertForbidden(await Replace(user, existingId, "overwritten"));
        }

        // nothing happened: no new entry, no new folder, the content is the original
        using var session = Repository.GetSession();
        var children = await _fixture.FileSystem.GetChildrenAsync(Repository, session, folder.Id);
        children.Select(c => c.Name).Should().BeEquivalentTo("keep.txt");
        (await Text(Manager, wkn, "Docs/keep.txt")).Should().Be("original");

        // FileManagement may do all of it
        (await Upload(Manager, wkn, "Docs/new.txt", "x")).Should().Match<IActionResult>(r => Status(r) == 201);
        (await Upload(Manager, wkn, "Docs/new2/deep.txt", "x", createFolders: true)).Should()
            .Match<IActionResult>(r => Status(r) == 201);
        (await Upload(Manager, wkn, "Docs/keep.txt", "v2", conflict: "replace")).Should()
            .Match<IActionResult>(r => Status(r) == 200);
        (await UploadIntoFolder(Manager, folder.Id.RtId.ToString(), "byid.txt", "x")).Should()
            .Match<IActionResult>(r => Status(r) == 201);
        (await Replace(Manager, existingId, "v3")).Should().Match<IActionResult>(r => Status(r) == 200);
        (await Text(Manager, wkn, "Docs/keep.txt")).Should().Be("v3");
    }

    // ---- GraphQL writes ----------------------------------------------------------------------------

    [Fact]
    public async Task GraphQlWrites_FileViewerAndNoFileRoleAreForbidden_FileManagementMayDoAll()
    {
        var root = await _helpers.CreateRootAsync();
        var folder = await _helpers.CreateFolderAsync(root, "Src");
        var target = await _helpers.CreateFolderAsync(root, "Dst");
        var file = await _helpers.UploadAsync(folder, "f.txt", "f", FileConflictMode.Fail);
        var sub = await _helpers.CreateFolderAsync(folder, "Sub");

        foreach (var user in new[] { Viewer, NoFileRole })
        {
            ErrorCode(await Run(CreateRoot, new { name = "R", wellKnownName = "Dn" + Guid.NewGuid().ToString("N")[..6] },
                user)).Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(CreateFolder, FolderVars("New", folder), user)).Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(RenameItem, new { rtId = Id(file), name = "renamed.txt" }, user))
                .Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(RenameFolder, new { rtId = Id(sub), name = "Renamed" }, user))
                .Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(MoveItem, MoveVars(file, folder, target), user)).Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(MoveFolder, MoveVars(sub, folder, target), user)).Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(Delete, new { rtEntityIds = new[] { Ref(file) } }, user)).Should().Be(FileSystemErrorCodes.Forbidden);
            ErrorCode(await Run(Delete, new { rtEntityIds = new[] { Ref(folder) } }, user)).Should().Be(FileSystemErrorCodes.Forbidden);
        }

        // nothing changed
        using (var session = Repository.GetSession())
        {
            (await _fixture.FileSystem.GetChildrenAsync(Repository, session, folder.Id)).Select(c => c.Name)
                .Should().BeEquivalentTo("f.txt", "Sub");
            (await _fixture.FileSystem.GetChildrenAsync(Repository, session, target.Id)).Should().BeEmpty();
        }

        (await Run(CreateFolder, FolderVars("New", folder), Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(RenameItem, new { rtId = Id(file), name = "renamed.txt" }, Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(RenameFolder, new { rtId = Id(sub), name = "Renamed" }, Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(MoveItem, MoveVars(file, folder, target), Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(MoveFolder, MoveVars(sub, folder, target), Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(Delete, new { rtEntityIds = new[] { Ref(folder) } }, Manager)).Errors.Should().BeNullOrEmpty();
        (await Run(Delete, new { rtEntityIds = new[] { Ref(target) } }, Manager)).Errors.Should().BeNullOrEmpty();
        var wkn = "Mg" + Guid.NewGuid().ToString("N")[..6];
        (await Run(CreateRoot, new { name = "R", wellKnownName = wkn }, Manager)).Errors.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task GraphQlReads_FileViewerSeesTheTree_NoFileRoleSeesNothing()
    {
        var root = await _helpers.CreateRootAsync();
        var folder = await _helpers.CreateFolderAsync(root, "A");
        await _helpers.UploadAsync(folder, "a.txt", "a", FileConflictMode.Fail);

        foreach (var user in new[] { Viewer, Manager })
        {
            var result = await Run(FolderChildren, new { rtId = Id(folder) }, user);
            result.Errors.Should().BeNullOrEmpty();
            JObject.Parse(_fixture.SerializeGraphQl(result))
                .SelectToken("data.runtime.systemFilesFolder.items[0].children.totalCount")!.Value<int>().Should().Be(1);
        }

        var hidden = await Run(FolderChildren, new { rtId = Id(folder) }, NoFileRole);
        JObject.Parse(_fixture.SerializeGraphQl(hidden)).SelectToken("data.runtime.systemFilesFolder.items")!
            .Should().BeEmpty("a caller without a file role does not see the entries (engine read filter)");
    }

    // ---- AB#6575: refused requests are visible -------------------------------------------------------

    [Fact]
    public async Task Denials_AreCounted_WithTenantAccountAndAction_AndAllowedCallsAreNot()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var folder = await _helpers.CreateFolderAsync(root, "Sig");
        var file = await _helpers.UploadAsync(folder, "s.txt", "s", FileConflictMode.Fail);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var viewer = FilesTestFixture.CreateUser("sig-viewer-" + suffix, "FileViewer");
        var nobody = FilesTestFixture.CreateUser("sig-nobody-" + suffix, "ReportingManagement");
        var manager = FilesTestFixture.CreateUser("sig-manager-" + suffix, "FileManagement");

        var seen = new global::System.Collections.Concurrent.ConcurrentBag<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new global::System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == Meshmakers.Octo.Backend.AssetRepositoryServices.Observability.FilesAccessMetrics.AccessDeniedMetricName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            seen.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        AssertForbidden(await Download(nobody, wkn, "Sig/s.txt"));                       // read
        AssertForbidden(await Upload(viewer, wkn, "Sig/new.txt", "x"));                  // write
        ErrorCode(await Run(Delete, new { rtEntityIds = new[] { Ref(file) } }, viewer)) // delete
            .Should().Be(FileSystemErrorCodes.Forbidden);

        // allowed calls and a plain 404 do not count
        (await Download(viewer, wkn, "Sig/s.txt")).Should().BeOfType<FileStreamResult>();
        (await Upload(manager, wkn, "Sig/ok.txt", "x")).Should().Match<IActionResult>(r => Status(r) == 201);
        (await Download(manager, wkn, "Sig/missing.txt")).Should().Match<IActionResult>(r => Status(r) == 404);

        var mine = seen.Where(m => ((string?)m.Tags["account"])?.EndsWith(suffix, StringComparison.Ordinal) == true).ToList();
        mine.Should().OnlyContain(m => m.Value == 1 && (string?)m.Tags["tenant_id"] == Repository.TenantId);
        mine.Select(m => ((string?)m.Tags["account"], (string?)m.Tags["action"])).Should().BeEquivalentTo(new[]
        {
            ("sig-nobody-" + suffix, "read"),
            ("sig-viewer-" + suffix, "write"),
            ("sig-viewer-" + suffix, "delete")
        });
    }

    // ---- tenant policies replace the baseline -------------------------------------------------------

    [Fact]
    public async Task TenantPolicies_ReplaceTheBaseline_AsTheBlueprintSeedsThem()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var editor = FilesTestFixture.CreateUser("editor", "ContentEditor");
        _fixture.Permissions.Table = BlueprintLikeTable(editorRole: "ContentEditor");

        // a custom role granted the manage permission may write, FileManagement (not listed) may not
        (await Upload(editor, wkn, "e.txt", "x")).Should().Match<IActionResult>(r => Status(r) == 201);
        AssertForbidden(await Upload(Manager, wkn, "m.txt", "x"));
        (await Download(Viewer, wkn, "e.txt")).Should().BeOfType<FileStreamResult>();
        AssertForbidden(await Upload(Viewer, wkn, "v.txt", "x"));
    }

    [Fact]
    public async Task SystemContext_IsNeverRestricted()
    {
        // migrations, the sweep and the blueprint apply write with the system context
        var root = await _helpers.CreateRootAsync();
        var entry = await _helpers.UploadAsync(root, "system.txt", "s", FileConflictMode.Fail);
        entry.Name.Should().Be("system.txt");
        var access = _fixture.GetService<FileSystemAccess>();
        await access.EnsureAsync(Repository, RtSecurityContext.System, RtDataAction.Write);
    }

    [Fact]
    public async Task Baseline_AppliesOnlyToTenantsWithSystemFiles_AndStepsAsideForTenantPolicies()
    {
        var empty = RtDataPolicyTable.Empty;
        var withBaseline = FileSystemPermissionBaseline.Apply(empty);
        withBaseline.Rules.Should().HaveCount(2);
        FileSystemPermissionBaseline.Apply(empty).Should().BeSameAs(withBaseline, "the combined table is cached");

        var tenantOwn = BlueprintLikeTable("ContentEditor");
        FileSystemPermissionBaseline.Apply(tenantOwn).Should().BeSameAs(tenantOwn);

        // an AuditOnly rule is ignored when enforcing, a leaf-only rule leaves the other types open: the baseline stays
        var auditOnly = new RtDataPolicyTable([new RtDataPolicyRule("files.audit",
            new HashSet<string> { FileSystemConstants.FileSystemEntityCkTypeId },
            [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete], false, true, new HashSet<string> { "X" })]);
        FileSystemPermissionBaseline.Apply(auditOnly).Rules.Should().HaveCount(3, "AuditOnly does not replace the baseline");
        var leafOnly = new RtDataPolicyTable([new RtDataPolicyRule("files.leaf",
            new HashSet<string> { FileSystemConstants.FileSystemItemCkTypeId },
            [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete], false, false, new HashSet<string> { "X" })]);
        FileSystemPermissionBaseline.Apply(leafOnly).Rules.Should().HaveCount(3, "a leaf-only rule does not replace the baseline");
        // an enforcing rule on the base type with only some actions replaces it (the engine denies the other actions)
        var readOnlyBase = new RtDataPolicyTable([new RtDataPolicyRule("files.read",
            new HashSet<string> { FileSystemConstants.FileSystemEntityCkTypeId },
            [RtDataAction.Read], false, false, new HashSet<string> { "X" })]);
        FileSystemPermissionBaseline.Apply(readOnlyBase).Should().BeSameAs(readOnlyBase);

        // a policy on identity types does not count as a file policy
        var identityOnly = IdentityAdministrationOnlyTable();
        FileSystemPermissionBaseline.Apply(identityOnly).Rules.Should().HaveCount(identityOnly.Rules.Count + 2);

        // the production wrapper: tenant with System.Files gets the baseline, one without does not
        var services = new ServiceCollection();
        services.AddSingleton<IDataPermissionResolver>(new FixedResolver(empty));
        services.AddSingleton(_fixture.GetService<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>());
        services.AddFileSystemPermissionBaseline();
        using var provider = services.BuildServiceProvider();
        var wrapped = provider.GetRequiredService<IDataPermissionResolver>();
        wrapped.Should().BeOfType<FileSystemBaselineDataPermissionResolver>();
        (await wrapped.GetPolicyTableAsync(Repository)).Rules.Should().HaveCount(2);
        // the system tenant has System.Files; the child test tenant does not
        var child = await _fixture.GetService<IOctoService>().SystemContext
            .FindTenantRepositoryAsync(_fixture.TestTenantId);
        (await wrapped.GetPolicyTableAsync(child)).Rules.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------

    private static string Id(FileSystemEntry entry) => entry.Id.RtId.ToString();

    private static object Ref(FileSystemEntry entry) =>
        new { ckTypeId = FileSystemService.SemanticName(entry.Id.CkTypeId), rtId = entry.Id.RtId.ToString() };

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

    /// <summary>The data policies System.Identity.Bootstrap seeds for System.Files, with a custom writer role.</summary>
    private static RtDataPolicyTable BlueprintLikeTable(string editorRole)
    {
        var targets = new HashSet<string> { FileSystemConstants.FileSystemEntityCkTypeId };
        return new RtDataPolicyTable(
        [
            new RtDataPolicyRule("files.read", targets, [RtDataAction.Read], false, false,
                new HashSet<string> { "FileViewer", "FileManagement", editorRole }),
            new RtDataPolicyRule("files.manage", targets, [RtDataAction.Write, RtDataAction.Delete], false, false,
                new HashSet<string> { editorRole })
        ]);
    }

    /// <summary>meshtest on test-2: only the identity.administration policy next to the files.</summary>
    private static RtDataPolicyTable IdentityAdministrationOnlyTable(bool auditOnly = false) => new(
    [
        new RtDataPolicyRule("identity.administration",
            new HashSet<string> { "System.Identity/User", "System.Identity/Role" },
            [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete], false, auditOnly,
            new HashSet<string> { "UserManagement" })
    ]);

    // ---- Interim tenant guard (AB#6579) ----------------------------------------------------------

    private static ClaimsPrincipal BearerUser(string? tenantId, bool withSubject = true, params string[] roles)
    {
        var claims = new List<Claim>();
        if (withSubject)
        {
            claims.Add(new Claim("sub", "tenant-guard-user"));
        }
        else
        {
            claims.Add(new Claim("client_id", "tenant-guard-client"));
        }

        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private async Task<IActionResult> CallWithBearer(ClaimsPrincipal user, Func<FilesController, Task<IActionResult>> call,
        string mode = "Enforce")
    {
        var (controller, http) = Create(user, mode: mode);

        http.Request.Headers.Authorization = "Bearer test-token-not-real";
        return await call(controller);
    }

    [Fact]
    public async Task TenantGuard_UserTokenOfAnotherTenant_Gets403_OnStatsAndCapabilities()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var foreign = BearerUser("some-other-tenant", true, "FileViewer", "FileManagement");

        AssertForbidden(await CallWithBearer(foreign, c => c.GetStatsByPath(wkn, null)));
        AssertForbidden(await CallWithBearer(foreign, c => c.GetCapabilities()));
        AssertForbidden(await CallWithBearer(foreign, c => c.DownloadByPath(wkn, "x.txt")));
        // A bearer user token without any tenant_id claim is refused as well (as the middleware under Enforce).
        AssertForbidden(await CallWithBearer(BearerUser(null, true, "FileViewer"),
            c => c.GetStatsByPath(wkn, null)));
    }

    [Fact]
    public async Task TenantGuard_ParentSiblingAndChildTokens_AreAllRefused()
    {
        // No parent exemption (as under the platform Enforce): only the exact tenant is accepted.
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        foreach (var other in new[] { "parent-x", "sibling-y", "child-x" })
        {
            var user = BearerUser(other, true, "FileViewer", "FileManagement");
            AssertForbidden(await CallWithBearer(user, c => c.GetStatsByPath(wkn, null)));
            AssertForbidden(await CallWithBearer(user, c => c.GetCapabilities()));
        }
    }

    [Fact]
    public async Task TenantGuard_ForeignToken_IsRefusedOnWriteRoutes_AndWritesNothing()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var foreign = BearerUser("some-other-tenant", true, "FileManagement");

        var (controller, http) = Create(foreign, "payload");
        http.Request.Headers.Authorization = "Bearer test-token-not-real";
        AssertForbidden(await controller.UploadByPath(wkn, "foreign.txt", null, false));

        // Same tenant: the identical upload works, proving the 403 above came from the guard.
        var (okController, okHttp) = Create(BearerUser(_fixture.GetSystemContext().TenantId, true, "FileManagement"),
            "payload");
        okHttp.Request.Headers.Authorization = "Bearer test-token-not-real";
        Status(await okController.UploadByPath(wkn, "own.txt", null, false)).Should().BeLessThan(300);
        Status(await Download(Manager, wkn, "foreign.txt")).Should().Be(StatusCodes.Status404NotFound);
        AssertForbidden(await CallWithBearer(foreign, c => c.DownloadByPath(wkn, "own.txt")));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("5")]
    [InlineData("")]
    [InlineData(null)]
    public async Task TenantGuard_InvalidConfiguredMode_FallsBackToEnforce(string? configured)
    {
        var root = await _helpers.CreateRootAsync();
        var foreign = BearerUser("some-other-tenant", true, "FileViewer");
        AssertForbidden(await CallWithBearer(foreign, c => c.GetStatsByPath(root.Entity.RtWellKnownName!, null),
            configured!));
    }

    [Fact]
    public async Task TenantGuard_LogOnlyMode_LetsAForeignTenantTokenPass()
    {
        var root = await _helpers.CreateRootAsync();
        var foreign = BearerUser("some-other-tenant", true, "FileViewer");
        (await CallWithBearer(foreign, c => c.GetStatsByPath(root.Entity.RtWellKnownName!, null),
            "LogOnly")).Should().BeOfType<OkObjectResult>();
        (await CallWithBearer(foreign, c => c.GetCapabilities(), "LogOnly"))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TenantGuard_SameTenantUserToken_StillWorks_CaseInsensitive()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;
        var tenantId = _fixture.GetSystemContext().TenantId;

        foreach (var tenant in new[] { tenantId, tenantId.ToUpperInvariant() })
        {
            var user = BearerUser(tenant, true, "FileViewer");
            (await CallWithBearer(user, c => c.GetStatsByPath(wkn, null))).Should().BeOfType<OkObjectResult>();
            (await CallWithBearer(user, c => c.GetCapabilities())).Should().BeOfType<OkObjectResult>();
        }
    }

    [Fact]
    public async Task TenantGuard_ServiceTokenWithoutSubject_AndCookiePrincipal_AreNotAffected()
    {
        var root = await _helpers.CreateRootAsync();
        var wkn = root.Entity.RtWellKnownName!;

        // Client-credentials token (no user subject) of another tenant: governed by the service-token
        // enforcement, not by this guard; the file roles still decide.
        var service = BearerUser("some-other-tenant", false, "FileViewer");
        (await CallWithBearer(service, c => c.GetStatsByPath(wkn, null))).Should().BeOfType<OkObjectResult>();
        (await CallWithBearer(service, c => c.GetCapabilities())).Should().BeOfType<OkObjectResult>();

        // Cookie principal (other authentication type, no bearer header): not touched.
        (await Stats(Viewer, wkn, null)).Should().BeOfType<OkObjectResult>();
        var cookie = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "c"), new Claim("tenant_id", "other"), new Claim(ClaimTypes.Role, "FileViewer")],
            "Identity.Application"));
        (await CallWithBearer(cookie, c => c.GetStatsByPath(wkn, null))).Should().BeOfType<OkObjectResult>();
    }

    private static void AssertForbidden(IActionResult result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (problem.Value as ProblemDetails)!.Extensions["code"].Should().Be(FileSystemErrorCodes.Forbidden);
    }

    private static int Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static string? ErrorCode(ExecutionResult result) => result.Errors?.FirstOrDefault()?.Code;

    private async Task<ExecutionResult> Run(string document, object variables, ClaimsPrincipal user)
    {
        var result = await _fixture.ExecuteGraphQlAsync(document, JsonSerializer.Serialize(variables), user);
        _fixture.OutputHelper?.WriteLine(_fixture.SerializeGraphQl(result));
        return result;
    }

    private Task<IActionResult> Download(ClaimsPrincipal user, string root, string path) =>
        Create(user).Controller.DownloadByPath(root, path);

    private Task<IActionResult> DownloadById(ClaimsPrincipal user, string rtId) =>
        Create(user).Controller.DownloadById(rtId);

    private Task<IActionResult> Stats(ClaimsPrincipal user, string root, string? path) =>
        Create(user).Controller.GetStatsByPath(root, path);

    private Task<IActionResult> StatsById(ClaimsPrincipal user, string rtId) =>
        Create(user).Controller.GetStatsById(rtId);

    private Task<IActionResult> LinkedCounts(ClaimsPrincipal user, string rtId) =>
        Create(user).Controller.GetLinkedCounts(new LinkedCountsRequestDto { RtIds = [rtId] });

    private async Task<IActionResult> Zip(ClaimsPrincipal user, string root, string path)
    {
        var (controller, http) = Create(user);
        var body = new MemoryStream();
        var bodyControl = new KestrelLikeBodyControl();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>(bodyControl);
        http.Response.Body = new KestrelLikeResponseBody(body, bodyControl);
        return await controller.DownloadZip(new ZipRequestDto { Items = [new FileRefDto { Root = root, Path = path }] });
    }

    private Task<IActionResult> Upload(ClaimsPrincipal user, string root, string path, string content,
        string? conflict = null, bool createFolders = false) =>
        Create(user, content).Controller.UploadByPath(root, path, conflict, createFolders);

    private Task<IActionResult> UploadIntoFolder(ClaimsPrincipal user, string folderRtId, string name, string content) =>
        Create(user, content).Controller.UploadIntoFolderById(folderRtId, name);

    private Task<IActionResult> Replace(ClaimsPrincipal user, string rtId, string content) =>
        Create(user, content).Controller.ReplaceById(rtId);

    private async Task<string> Text(ClaimsPrincipal user, string root, string path)
    {
        var file = (FileStreamResult)await Download(user, root, path);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private (FilesController Controller, DefaultHttpContext Http) Create(ClaimsPrincipal user, string? body = null,
        string mode = "Enforce")
    {
        var http = new DefaultHttpContext { User = user, RequestServices = _fixture.Provider! };
        http.Request.RouteValues["tenantId"] = _fixture.GetSystemContext().TenantId;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
            http.Request.ContentType = "text/plain";
        }

        var controller = new FilesController(_fixture.GetService<IOctoService>(), _fixture.FileSystem,
            _fixture.GetService<FileZipService>(), _fixture.GetService<FileSystemAccess>(),
            Options.Create(new FilesOptions { TenantGuard = mode }),
            _fixture.GetService<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, http);
    }

    private sealed class FixedResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(
            Meshmakers.Octo.Runtime.Contracts.Repositories.IRuntimeRepository runtimeRepository) =>
            Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }
}
