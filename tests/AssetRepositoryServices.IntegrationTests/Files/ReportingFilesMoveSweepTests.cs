using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6175 (AB#6171 S3): the raw move of System.Reporting file data to System.Files, its idempotence and
///     straggler handling, the R1 fixes (download gate and cascade delete key on the rewritten GridFS stamp)
///     and the pre-check report.
/// </summary>
[Collection(FilesMigrationCollection.Name)]
public class ReportingFilesMoveSweepTests
{
    private const string Legacy = ReportingFilesMigrationConstants.LegacyEntityCollectionName;
    private const string Target = FileSystemConstants.EntityCollectionName;
    private const string Associations = ReportingFilesMigrationConstants.AssociationCollectionName;
    private const string Audit = ReportingFilesMigrationConstants.AuditCollectionName;
    private const string TenantSweepLeaseCollection = ReportingFilesMigrationConstants.LeaseCollectionName;

    private readonly FilesMigrationTestFixture _fixture;

    public ReportingFilesMoveSweepTests(FilesMigrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string TenantId => _fixture.GetSystemContext().TenantId;

    private ITenantRepository Repository => _fixture.GetSystemContext().GetTenantRepository();

    [Fact]
    public async Task Sweep_MovesEntitiesAssociationsAndStamps_SecondRunWritesNothing()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var auditBefore = await _fixture.Collection(Audit).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: Ct);

        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);

        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));
        result.Before!.FileSystemItems.Should().Be(3);
        result.Before.Folders.Should().Be(1);
        result.Before.FolderRoots.Should().Be(1);
        result.Before.FileStamps.Should().Be(3);
        result.EntitiesMoved.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId] = 3,
            [ReportingFilesMigrationConstants.LegacyFolderCkTypeId] = 1,
            [ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId] = 1
        });
        // ParentChild: folder->root, a->folder, b->root (3 origins, 3 targets); Related customer->a (1 target).
        result.AssociationOriginsUpdated.Should().Be(3);
        result.AssociationTargetsUpdated.Should().Be(4);
        result.StampsUpdated.Should().Be(3);
        result.Conflicts.Should().BeEmpty();
        result.After!.IsZero.Should().BeTrue();

        (await _fixture.Collection(Legacy).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct))
            .Should().Be(0, "the legacy collection is emptied but never dropped (a straggler could be lost)");

        // Entities: same _id, new ckTypeId, content reference unchanged.
        var moved = await _fixture.Collection(Target).Find(Builders<BsonDocument>.Filter.In("_id",
                new[] { tree.Root, tree.Folder, tree.ItemA, tree.ItemB, tree.Orphan }))
            .ToListAsync(Ct);
        moved.Should().HaveCount(5);
        moved.Single(d => d["_id"] == tree.Root)["ckTypeId"].AsString.Should().Be(FileSystemConstants.FolderRootCkTypeId);
        moved.Single(d => d["_id"] == tree.Root)["rtWellKnownName"].AsString.Should().Be(tree.RootWellKnownName);
        moved.Single(d => d["_id"] == tree.Folder)["ckTypeId"].AsString.Should().Be(FileSystemConstants.FolderCkTypeId);
        var itemA = moved.Single(d => d["_id"] == tree.ItemA);
        itemA["ckTypeId"].AsString.Should().Be(FileSystemConstants.FileSystemItemCkTypeId);
        itemA["attributes"]["content"]["binaryId"].AsObjectId.Should().Be(tree.BinaryA);
        itemA["attributes"]["name"].AsString.Should().Be("a.txt");

        // Associations.
        var related = await _fixture.Collection(Associations).Find(
            Builders<BsonDocument>.Filter.Eq("targetRtId", tree.ItemA) &
            Builders<BsonDocument>.Filter.Eq("associationRoleId", "System/Related")).SingleAsync(Ct);
        related["targetCkTypeId"].AsString.Should().Be(FileSystemConstants.FileSystemItemCkTypeId);
        related["originCkTypeId"].AsString.Should().Be(FilesMigrationTestFixture.CustomerCkTypeId);
        var parentOfA = await _fixture.Collection(Associations).Find(
            Builders<BsonDocument>.Filter.Eq("originRtId", tree.ItemA) &
            Builders<BsonDocument>.Filter.Eq("associationRoleId", "System/ParentChild")).SingleAsync(Ct);
        parentOfA["originCkTypeId"].AsString.Should().Be(FileSystemConstants.FileSystemItemCkTypeId);
        parentOfA["targetCkTypeId"].AsString.Should().Be(FileSystemConstants.FolderCkTypeId);

        // GridFS stamps.
        var stamp = await _fixture.Collection("fs.files").Find(Builders<BsonDocument>.Filter.Eq("_id", tree.BinaryA))
            .SingleAsync(Ct);
        stamp["metadata"]["rtEntityId"].AsString.Should().Be($"{FileSystemConstants.FileSystemItemCkTypeId}@{tree.ItemA}");

        // Audit record.
        var audits = await _fixture.Collection(Audit).Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("executedAt")).ToListAsync(Ct);
        audits.Should().HaveCount((int)auditBefore + 1);
        audits[0]["outcome"].AsString.Should().Be(nameof(ReportingFilesSweepOutcome.Moved));
        audits[0]["entitiesMovedTotal"].ToInt64().Should().Be(5);
        audits[0]["trigger"].AsString.Should().Be("Test");

        // Second run: nothing to do, no writes (no audit record, no collection re-created).
        var second = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        second.Outcome.Should().Be(ReportingFilesSweepOutcome.NothingToDo);
        second.TotalEntitiesMoved.Should().Be(0);
        (await _fixture.Collection(Audit).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: Ct)).Should().Be(auditBefore + 1);
        (await _fixture.CollectionExistsAsync(TenantSweepLeaseCollection)).Should().BeTrue();
        (await _fixture.Collection(TenantSweepLeaseCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: Ct)).Should().Be(0, "the lease is released after the sweep");
    }

    [Fact]
    public async Task AfterTheMove_FileSystemServiceResolvesTheTreeByPath()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var reportingRoot = await _fixture.InsertLegacyEntityAsync(
            ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId, "Reporting assets", "ReportingAssets_test");

        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));

        using var session = Repository.GetSession();
        var a = await _fixture.FileSystem.ResolveAsync(Repository, session, tree.RootWellKnownName, "Docs/a.txt");
        a.Id.RtId.ToString().Should().Be(tree.ItemA.ToString());
        a.Kind.Should().Be(FileSystemEntryKind.File);
        a.Content!.BinaryId.ToString().Should().Be(tree.BinaryA.ToString());

        var b = await _fixture.FileSystem.ResolveAsync(Repository, session, tree.RootWellKnownName, "B.TXT");
        b.Id.RtId.ToString().Should().Be(tree.ItemB.ToString());

        var root = await _fixture.FileSystem.FindRootAsync(Repository, session, "ReportingAssets_test");
        root.Should().NotBeNull("Reporting roots move like any folder root");
        root!.Id.RtId.ToString().Should().Be(reportingRoot.ToString());
        FileSystemConstants.IsServiceOwnedRoot(root.Entity.RtWellKnownName).Should().BeTrue();
    }

    [Fact]
    public async Task R1_DownloadGate_EnforcesAPolicyOnSystemFilesOnlyAfterTheStampRewrite()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var resolver = new StaticDataPermissionResolver(new RtDataPolicyTable(
        [
            new RtDataPolicyRule("files.read",
                new HashSet<string> { new RtCkId<CkTypeId>(FileSystemConstants.FileSystemItemCkTypeId).SemanticVersionedFullName },
                [RtDataAction.Read], OwnedOnly: false, AuditOnly: false, new HashSet<string> { "FileManagement" })
        ]));
        var plainUser = FilesTestFixture.CreateUser("plain-user");
        var fileManager = FilesTestFixture.CreateUser("file-manager", "FileManagement");

        // Negative control: with the legacy stamp the owner type is unknown, the policy never matches — fails open.
        (await DownloadAsync(resolver, plainUser, tree.BinaryA)).Should().BeOfType<FileStreamResult>(
            "the legacy stamp names a type the CK cache does not know, so the gate cannot apply the policy");

        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));

        (await DownloadAsync(resolver, plainUser, tree.BinaryA)).Should().BeOfType<NotFoundObjectResult>(
            "after the stamp rewrite the policy on System.Files/FileSystemItem denies the download");
        (await DownloadAsync(resolver, fileManager, tree.BinaryA)).Should().BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task R1_CascadeDelete_ErasingAMovedItemRemovesItsGridFsFile()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));

        using (var session = await Repository.GetSessionAsync())
        {
            session.StartTransaction();
            await Repository.DeleteOneRtEntityByRtIdAsync(session, FileSystemService.FileType,
                OctoObjectId.Parse(tree.ItemB.ToString()), Runtime.Contracts.Repositories.DeleteOptions.Erase);
            await session.CommitTransactionAsync();
        }

        (await _fixture.Collection("fs.files").CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", tree.BinaryB),
            cancellationToken: Ct)).Should().Be(0, "the cascade delete matches the rewritten stamp");
        (await _fixture.Collection("fs.files").CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", tree.BinaryA),
            cancellationToken: Ct)).Should().Be(1, "other files are untouched");
    }

    [Fact]
    public async Task Straggler_WrittenAfterTheFirstSweep_IsMovedByTheTimerAndTheTenantLeavesTheTimer()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var start = await _fixture.Runner.RunAtTenantStartAsync(TenantId, Ct);
        start!.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", start.Errors));
        _fixture.Tracker.IsPending(TenantId).Should().BeTrue("a tenant with moved data stays on the straggler timer");

        // An old writer adds another legacy file under the (already moved) folder.
        var (straggler, binary) = await _fixture.InsertLegacyItemAsync("late.txt", "late", tree.Folder,
            ReportingFilesMigrationConstants.LegacyFolderCkTypeId);

        await _fixture.Runner.RunStragglerSweepAsync(Ct);

        var movedStraggler = await _fixture.Collection(Target).Find(Builders<BsonDocument>.Filter.Eq("_id", straggler))
            .SingleAsync(Ct);
        movedStraggler["ckTypeId"].AsString.Should().Be(FileSystemConstants.FileSystemItemCkTypeId);
        var stamp = await _fixture.Collection("fs.files").Find(Builders<BsonDocument>.Filter.Eq("_id", binary))
            .SingleAsync(Ct);
        stamp["metadata"]["rtEntityId"].AsString.Should().StartWith(FileSystemConstants.FileSystemItemCkTypeId + "@");
        (await _fixture.Collection(Associations).CountDocumentsAsync(
                Builders<BsonDocument>.Filter.In("originCkTypeId", ReportingFilesMigrationConstants.LegacyTypeIds) |
                Builders<BsonDocument>.Filter.In("targetCkTypeId", ReportingFilesMigrationConstants.LegacyTypeIds),
                cancellationToken: Ct))
            .Should().Be(0);
        _fixture.Tracker.IsPending(TenantId).Should().BeTrue("the straggler run moved data, so the timer keeps watching");

        using (var session = Repository.GetSession())
        {
            var late = await _fixture.FileSystem.ResolveAsync(Repository, session, tree.RootWellKnownName, "Docs/late.txt");
            late.Id.RtId.ToString().Should().Be(straggler.ToString());
        }

        await _fixture.Runner.RunStragglerSweepAsync(Ct);
        _fixture.Tracker.IsPending(TenantId).Should().BeTrue(
            "a zero check inside the straggler window keeps the tenant on the timer (old writers may still appear)");

        _fixture.Tracker.SetLastFinding(TenantId, DateTime.UtcNow - TimeSpan.FromHours(25));
        await _fixture.Runner.RunStragglerSweepAsync(Ct);
        _fixture.Tracker.IsPending(TenantId).Should().BeFalse("a zero check after the window takes the tenant off the timer");
    }

    [Fact]
    public async Task PreCheck_ListsLiteralReferencesOrphansAndCounts()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var pipelines = _fixture.Collection("RtEntity_SystemCommunicationDeployableEntity");
        var pipelineId = ObjectId.GenerateNewId();
        await pipelines.InsertOneAsync(new BsonDocument
        {
            { "_id", pipelineId },
            { "_t", "RtEntity" },
            { "ckTypeId", "System.Communication/Pipeline" },
            { "rtWellKnownName", "ImportReceipts" },
            {
                "attributes", new BsonDocument
                {
                    { "name", "Import receipts" },
                    { "rtBlueprintSource", "MeshmakersApp-2.2.0" },
                    {
                        "pipelineDefinition",
                        "transformations:\n  - type: CreateUpdateInfo@1\n    ckTypeId: System.Reporting/FileSystemItem\n" +
                        "  - type: CreateAssociationUpdate@1\n    targetCkTypeId: System.Reporting-2.3.0/FolderRoot-1\n"
                    }
                }
            }
        }, cancellationToken: Ct);
        var queryId = ObjectId.GenerateNewId();
        var queries = _fixture.Collection("RtEntity_SystemPersistentQuery");
        await queries.InsertOneAsync(new BsonDocument
        {
            { "_id", queryId },
            { "_t", "RtEntity" },
            { "ckTypeId", "System/PersistentQuery" },
            { "attributes", new BsonDocument { { "name", "q" }, { "tags", new BsonArray { "x", "System.Reporting/Folder" } } } }
        }, cancellationToken: Ct);
        var unrelatedId = ObjectId.GenerateNewId();
        await queries.InsertOneAsync(new BsonDocument
        {
            { "_id", unrelatedId },
            { "_t", "RtEntity" },
            { "ckTypeId", "System/PersistentQuery" },
            { "attributes", new BsonDocument { { "name", "System.Reporting/ConnectionInfo is not a file type" } } }
        }, cancellationToken: Ct);

        try
        {
            var status = await _fixture.Status.GetStatusAsync(TenantId, Ct);

            status.Should().NotBeNull();
            status!.Legacy.FileSystemItems.Should().Be(3);
            status.Legacy.FileStamps.Should().Be(3);
            status.TargetModelReady.Should().BeTrue();
            status.OrphanCount.Should().Be(1);
            status.Orphans.Should().ContainSingle()
                .Which.Should().Be($"{ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId}@{tree.Orphan}");

            status.LiteralReferences.Select(r => r.RtId).Should()
                .BeEquivalentTo([pipelineId.ToString(), queryId.ToString()]);
            status.LiteralReferenceCount.Should().Be(2);
            var pipeline = status.LiteralReferences.Single(r => r.RtId == pipelineId.ToString());
            pipeline.CollectionName.Should().Be("RtEntity_SystemCommunicationDeployableEntity");
            pipeline.CkTypeId.Should().Be("System.Communication/Pipeline");
            pipeline.RtWellKnownName.Should().Be("ImportReceipts");
            pipeline.RtBlueprintSource.Should().Be("MeshmakersApp-2.2.0");
            pipeline.Literals.Should().BeEquivalentTo(["System.Reporting/FileSystemItem", "System.Reporting/FolderRoot"]);
            pipeline.FieldPaths.Should().BeEquivalentTo(["attributes.pipelineDefinition"]);
            var query = status.LiteralReferences.Single(r => r.RtId == queryId.ToString());
            query.Literals.Should().BeEquivalentTo(["System.Reporting/Folder"]);
            query.FieldPaths.Should().BeEquivalentTo(["attributes.tags[1]"]);
            query.RtBlueprintSource.Should().BeNull();
            status.ScannedCollections.Should().BeGreaterThan(2);

            // The system endpoint returns the same report to a system administrator only.
            var controller = new FilesMigrationController(_fixture.Status, _fixture.GetSystemContext(),
                NullLogger<FilesMigrationController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = SystemAdmin() } }
            };
            var ok = await controller.GetMigrationStatus(TenantId, Ct);
            ok.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<FilesMigrationStatusDto>()
                .Which.LiteralReferenceCount.Should().Be(2);
            (await controller.GetMigrationStatus("does-not-exist-" + Guid.NewGuid().ToString("N"), Ct))
                .Should().BeOfType<NotFoundObjectResult>();

            controller.ControllerContext.HttpContext.User = FilesTestFixture.CreateUser("someone", "AdminPanelManagement");
            (await controller.GetMigrationStatus(TenantId, Ct)).Should().BeOfType<ForbidResult>(
                "a token of another tenant must not read other tenants' reports");
        }
        finally
        {
            await pipelines.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", pipelineId), Ct);
            await queries.DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", new[] { queryId, unrelatedId }), Ct);
            await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        }
    }

    [Fact]
    public async Task TenantWithoutReportingData_IsANoOp_AndAMissingTargetModelBlocksTheMove()
    {
        // A database without any file data: nothing to do, nothing created.
        var emptyDatabase = new MongoClient(_fixture.GetConnectionString())
            .GetDatabase("filesmigration" + Guid.NewGuid().ToString("N"));
        var sweep = new ReportingFilesMoveSweep(new FixedDatabaseProvider(emptyDatabase),
            new StaticOptionsMonitor(new FilesMigrationOptions()), NullLogger<ReportingFilesMoveSweep>.Instance);

        var empty = await sweep.SweepAsync("t", "Test", Ct);
        empty.Outcome.Should().Be(ReportingFilesSweepOutcome.NothingToDo);
        (await (await emptyDatabase.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct)).Should().BeEmpty();
        (await ReportingFilesMoveSweep.IsReporting3InstalledAsync(emptyDatabase, Ct)).Should().BeFalse();

        // Legacy data but no System.Files: skipped, legacy data untouched.
        await emptyDatabase.GetCollection<BsonDocument>(Legacy).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "ckTypeId", ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId },
            { "attributes", new BsonDocument("name", "r") }
        }, cancellationToken: Ct);
        try
        {
            var blocked = await sweep.SweepAsync("t", "Test", Ct);
            blocked.Outcome.Should().Be(ReportingFilesSweepOutcome.TargetModelMissing);
            (await emptyDatabase.GetCollection<BsonDocument>(Legacy)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
            (await (await emptyDatabase.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
                .Should().BeEquivalentTo([Legacy]);

            var ckModels = emptyDatabase.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.CkModelCollectionName);
            await ckModels.InsertOneAsync(new BsonDocument("_id", "System.Reporting-2.3.0"), cancellationToken: Ct);
            (await ReportingFilesMoveSweep.IsReporting3InstalledAsync(emptyDatabase, Ct)).Should().BeFalse();
            await ckModels.InsertOneAsync(new BsonDocument("_id", "System.Reporting-3.0.0"), cancellationToken: Ct);
            (await ReportingFilesMoveSweep.IsReporting3InstalledAsync(emptyDatabase, Ct)).Should().BeTrue();
        }
        finally
        {
            await emptyDatabase.Client.DropDatabaseAsync(emptyDatabase.DatabaseNamespace.DatabaseName, Ct);
        }
    }

    [Fact]
    public async Task TenantStart_WithoutLegacyData_RunsNoScan()
    {
        // Make sure nothing is left from other tests.
        await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        var scansBefore = _fixture.Status.LiteralScanCount;

        var result = await _fixture.Runner.RunAtTenantStartAsync(TenantId, Ct);

        result!.Outcome.Should().Be(ReportingFilesSweepOutcome.NothingToDo);
        _fixture.Status.LiteralScanCount.Should().Be(scansBefore,
            "the start path runs only the cheap check when there is nothing to move");

        // With legacy data the lease holder scans once.
        await _fixture.SeedLegacyTreeAsync();
        var moved = await _fixture.Runner.RunAtTenantStartAsync(TenantId, Ct);
        moved!.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", moved.Errors));
        _fixture.Status.LiteralScanCount.Should().Be(scansBefore + 1);
    }

    [Fact]
    public async Task Lease_HeldByAnotherInstance_SkipsQuietly_AndAnExpiredLeaseIsTakenOver()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var other = await TenantSweepLease.TryAcquireAsync(_fixture.Database, ReportingFilesMoveSweep.LeaseName,
            TimeSpan.FromMinutes(5), Ct);
        other.Should().NotBeNull();
        try
        {
            (await TenantSweepLease.TryAcquireAsync(_fixture.Database, ReportingFilesMoveSweep.LeaseName,
                TimeSpan.FromMinutes(5), Ct)).Should().BeNull("only one holder at a time");

            var skipped = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
            skipped.Outcome.Should().Be(ReportingFilesSweepOutcome.LeaseHeld);
            skipped.TotalEntitiesMoved.Should().Be(0);
            (await _fixture.Collection(Legacy).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", tree.ItemA),
                cancellationToken: Ct)).Should().Be(1, "nothing moves without the lease");
        }
        finally
        {
            await other!.DisposeAsync();
        }

        // A crashed holder: its lease has expired and is taken over.
        await _fixture.Collection(TenantSweepLeaseCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ReportingFilesMoveSweep.LeaseName },
            { "owner", "crashed-pod/1" },
            { "expiresAt", DateTime.UtcNow.AddMinutes(-1) }
        }, cancellationToken: Ct);

        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task StaleWriter_ReWritingAMovedRtId_DoesNotOverwriteTheTarget()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        (await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct)).Outcome.Should().Be(ReportingFilesSweepOutcome.Moved);

        // An old writer re-creates b.txt under its legacy type with the same rtId.
        await _fixture.Collection(Legacy).InsertOneAsync(new BsonDocument
        {
            { "_id", tree.ItemB },
            { "_t", "RtEntity" },
            { "ckTypeId", ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId },
            { "attributes", new BsonDocument("name", "stale.txt") }
        }, cancellationToken: Ct);

        var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);

        result.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", result.Errors));
        result.Conflicts.Should().BeEquivalentTo([$"{ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId}@{tree.ItemB}"]);
        result.TotalEntitiesMoved.Should().Be(0);
        var target = await _fixture.Collection(Target).Find(Builders<BsonDocument>.Filter.Eq("_id", tree.ItemB)).SingleAsync(Ct);
        target["attributes"]["name"].AsString.Should().Be("b.txt", "the System.Files version is kept");
        var parked = await _fixture.Collection(ReportingFilesMigrationConstants.ConflictCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("rtId", tree.ItemB)).SingleAsync(Ct);
        parked["document"]["attributes"]["name"].AsString.Should().Be("stale.txt");
        result.After!.IsZero.Should().BeTrue();

        var audit = await _fixture.Collection(Audit).Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("executedAt")).FirstAsync(Ct);
        audit["conflicts"].AsBsonArray.Should().HaveCount(1);
    }

    [Fact]
    public async Task RootConflict_AbortsTheMove_IsReportedAndAuditedOnce_OtherLegacyTypesAreReported()
    {
        var tree = await _fixture.SeedLegacyTreeAsync();
        var clash = await _fixture.InsertLegacyEntityAsync(ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId,
            "files", "files");
        var derivedId = ObjectId.GenerateNewId();
        await _fixture.Collection(Legacy).InsertOneAsync(new BsonDocument
        {
            { "_id", derivedId }, { "_t", "RtEntity" }, { "ckTypeId", "Custom.Model/SpecialItem" },
            { "attributes", new BsonDocument("name", "special") }
        }, cancellationToken: Ct);
        try
        {
            var auditBefore = await _fixture.Collection(Audit).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                cancellationToken: Ct);

            var result = await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct);
            result.Outcome.Should().Be(ReportingFilesSweepOutcome.RootConflict);
            result.RootConflicts.Should().ContainSingle().Which.Should().Contain(clash.ToString()).And.Contain("'Files'");
            result.TotalEntitiesMoved.Should().Be(0);
            (await _fixture.Collection(Legacy).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", tree.ItemA),
                cancellationToken: Ct)).Should().Be(1, "nothing moves while a root collides");

            (await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct)).Outcome.Should().Be(ReportingFilesSweepOutcome.RootConflict);
            (await _fixture.Collection(Audit).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                cancellationToken: Ct)).Should().Be(auditBefore + 1, "a persistent conflict is audited once");

            var status = await _fixture.Status.GetStatusAsync(TenantId, Ct);
            status!.RootConflicts.Should().ContainSingle();
            status.OtherLegacyTypes.Should().ContainKey("Custom.Model/SpecialItem").WhoseValue.Should().Be(1);
            status.LiteralScanComplete.Should().BeTrue();
        }
        finally
        {
            await _fixture.Collection(Legacy).DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", new[] { clash, derivedId }), Ct);
        }

        (await _fixture.Sweep.SweepAsync(TenantId, "Test", Ct)).Outcome.Should().Be(ReportingFilesSweepOutcome.Moved);
    }

    [Fact]
    public void FindLiterals_RecognizesPlainAndVersionedTypeIds_Only()
    {
        static IReadOnlySet<string> Find(string text) =>
            FilesMigrationStatusService.FindLiterals(global::System.Text.Encoding.UTF8.GetBytes(text));

        Find("ckTypeId: System.Reporting/FileSystemItem").Should().BeEquivalentTo(["System.Reporting/FileSystemItem"]);
        Find("System.Reporting-2.3.0/FolderRoot-1 and System.Reporting/Folder")
            .Should().BeEquivalentTo(["System.Reporting/FolderRoot", "System.Reporting/Folder"]);
        Find("System.Reporting/FileSystemEntity").Should().BeEquivalentTo(["System.Reporting/FileSystemEntity"]);
        Find("System.Reporting/ConnectionInfo System.Reporting/FolderX System.Files/Folder").Should().BeEmpty();
        Find("System.Reporting").Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<IActionResult> DownloadAsync(IDataPermissionResolver resolver, ClaimsPrincipal user,
        ObjectId binaryId)
    {
        var controller = new LargeBinariesController(_fixture.GetService<IOctoService>(), resolver,
            _fixture.GetService<ICkCacheService>());
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var result = await controller.Get(binaryId.ToString());
        if (result is FileStreamResult stream)
        {
            await stream.FileStream.DisposeAsync();
        }

        return result;
    }

    private ClaimsPrincipal SystemAdmin()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "system-admin"),
            new Claim("tenant_id", TenantId),
            new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
        ], "IntegrationTests");
        return new ClaimsPrincipal(identity);
    }

    private sealed class StaticDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) =>
            Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }

    private sealed class FixedDatabaseProvider(IMongoDatabase database) : ITenantMongoDatabaseProvider
    {
        public Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId) => Task.FromResult<IMongoDatabase?>(database);
    }

    private sealed class StaticOptionsMonitor(FilesMigrationOptions value) : IOptionsMonitor<FilesMigrationOptions>
    {
        public FilesMigrationOptions CurrentValue => value;

        public FilesMigrationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FilesMigrationOptions, string?> listener) => null;
    }
}
