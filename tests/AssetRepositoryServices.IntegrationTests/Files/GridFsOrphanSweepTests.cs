using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.OrphanSweep;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6541: the orphan sweep endpoint against a real MongoDB database with the engine's sweeper. Referenced bytes
///     survive, a dry run (the default) deletes nothing, removal respects the grace period, the report numbers are
///     exact, a second run finds nothing, unauthorised callers get 403.
/// </summary>
[Collection(FilesRawMongoCollection.Name)]
public class GridFsOrphanSweepTests
{
    private const string EntityCollection = "RtEntity_OrphanSweepTest";

    private readonly FilesRawMongoFixture _fixture;

    public GridFsOrphanSweepTests(FilesRawMongoFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string TenantId => _fixture.GetSystemContext().TenantId;

    private FilesOrphanSweepController CreateController(ClaimsPrincipal user) =>
        new(_fixture.GetService<IGridFsOrphanSweepService>(), _fixture.GetSystemContext(),
            NullLogger<FilesOrphanSweepController>.Instance, new HostLifetimeStub())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

    private sealed class HostLifetimeStub : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private ClaimsPrincipal SystemAdmin() => new(new ClaimsIdentity(
    [
        new Claim("sub", "system-admin"),
        new Claim("tenant_id", TenantId),
        new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
    ], "IntegrationTests"));

    /// <summary>
    ///     Uploads one FileSystem file (one chunk), backdated by <paramref name="age" />.
    /// </summary>
    private async Task<ObjectId> UploadAsync(TimeSpan age, int size = 100)
    {
        var id = await new GridFSBucket(_fixture.Database).UploadFromBytesAsync("sweep.bin", new byte[size],
            new GridFSUploadOptions
            {
                Metadata = new BsonDocument { { "contentType", "application/octet-stream" }, { "binaryType", 0 } }
            }, Ct);
        await _fixture.Database.GetCollection<BsonDocument>("fs.files").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("uploadDate", DateTime.UtcNow - age), cancellationToken: Ct);
        return id;
    }

    private async Task ReferenceAsync(ObjectId binaryId)
    {
        await _fixture.Collection(EntityCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "ckTypeId", "Test/Owner" },
            { "attributes", new BsonDocument("content", new BsonDocument { { "binaryId", binaryId }, { "size", 100 } }) }
        }, cancellationToken: Ct);
    }

    private async Task<bool> FileExistsAsync(ObjectId id) =>
        await _fixture.Database.GetCollection<BsonDocument>("fs.files")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id), cancellationToken: Ct) > 0;

    private async Task<long> ChunksAsync(ObjectId id) =>
        await _fixture.Database.GetCollection<BsonDocument>("fs.chunks")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("files_id", id), cancellationToken: Ct);

    private async Task CleanAsync(params ObjectId[] ids)
    {
        var bucket = new GridFSBucket(_fixture.Database);
        foreach (var id in ids)
        {
            try
            {
                await bucket.DeleteAsync(id, Ct);
            }
            catch (GridFSFileNotFoundException)
            {
                // removed by the sweep
            }
        }

        await _fixture.Database.DropCollectionAsync(EntityCollection, Ct);
    }

    [Fact]
    public async Task DryRunIsTheDefault_ReportsTheOrphans_AndDeletesNothing()
    {
        var referenced = await UploadAsync(TimeSpan.FromDays(3));
        var orphan = await UploadAsync(TimeSpan.FromDays(3), 300);
        var young = await UploadAsync(TimeSpan.FromMinutes(2));
        await ReferenceAsync(referenced);
        try
        {
            // No remove and no grace parameter: the endpoint defaults are a dry run with 24 h.
            var result = await CreateController(SystemAdmin()).SweepOrphans(TenantId, cancellationToken: Ct);

            var report = result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<GridFsOrphanSweepReportDto>().Subject;
            report.TenantId.Should().Be(TenantId);
            report.Mode.Should().Be("dry-run");
            report.GracePeriodMinutes.Should().Be(1440);
            report.ExaminedFiles.Should().BeGreaterThanOrEqualTo(2);
            report.OrphanFiles.Should().Be(1);
            report.OrphanChunks.Should().Be(1);
            report.OrphanBytes.Should().Be(300);
            report.RemovedFiles.Should().Be(0);
            report.YoungFiles.Should().BeGreaterThanOrEqualTo(1);
            report.DurationMilliseconds.Should().BeGreaterThanOrEqualTo(0);

            (await FileExistsAsync(orphan)).Should().BeTrue("a dry run deletes nothing");
            (await ChunksAsync(orphan)).Should().Be(1);
            (await FileExistsAsync(referenced)).Should().BeTrue();
            (await FileExistsAsync(young)).Should().BeTrue();
        }
        finally
        {
            await CleanAsync(referenced, orphan, young);
        }
    }

    [Fact]
    public async Task Remove_DeletesOnlyOldOrphans_ReferencedAndYoungBytesSurvive_SecondRunRemovesNothing()
    {
        var referenced = await UploadAsync(TimeSpan.FromDays(3));
        var orphanA = await UploadAsync(TimeSpan.FromDays(3), 100);
        var orphanB = await UploadAsync(TimeSpan.FromDays(2), 250);
        var young = await UploadAsync(TimeSpan.FromMinutes(2));
        await ReferenceAsync(referenced);
        try
        {
            var controller = CreateController(SystemAdmin());
            var result = await controller.SweepOrphans(TenantId, remove: true, cancellationToken: Ct);

            var report = result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<GridFsOrphanSweepReportDto>().Subject;
            report.Mode.Should().Be("remove");
            report.OrphanFiles.Should().Be(2);
            report.RemovedFiles.Should().Be(2);
            report.OrphanChunks.Should().Be(2);
            report.OrphanBytes.Should().Be(350);

            (await FileExistsAsync(orphanA)).Should().BeFalse();
            (await FileExistsAsync(orphanB)).Should().BeFalse();
            (await ChunksAsync(orphanA)).Should().Be(0);
            (await ChunksAsync(orphanB)).Should().Be(0);
            (await FileExistsAsync(referenced)).Should().BeTrue("referenced bytes are never orphans");
            (await ChunksAsync(referenced)).Should().Be(1);
            (await FileExistsAsync(young)).Should().BeTrue("younger than the grace period");

            var second = await controller.SweepOrphans(TenantId, remove: true, cancellationToken: Ct);
            var again = second.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<GridFsOrphanSweepReportDto>().Subject;
            again.OrphanFiles.Should().Be(0);
            again.RemovedFiles.Should().Be(0);
        }
        finally
        {
            await CleanAsync(referenced, orphanA, orphanB, young);
        }
    }

    [Fact]
    public async Task GracePeriod_IsRespected_AndPassedThrough()
    {
        var orphan = await UploadAsync(TimeSpan.FromHours(2), 64);
        try
        {
            var controller = CreateController(SystemAdmin());

            // 2 h old, grace 3 h: not examined, survives even with remove=true.
            var kept = (GridFsOrphanSweepReportDto)((OkObjectResult)await controller.SweepOrphans(TenantId,
                remove: true, gracePeriodMinutes: 180, cancellationToken: Ct)).Value!;
            kept.GracePeriodMinutes.Should().Be(180);
            kept.OrphanFiles.Should().Be(0);
            kept.RemovedFiles.Should().Be(0);
            (await FileExistsAsync(orphan)).Should().BeTrue("younger than the grace period");

            // Grace 60 minutes: the file is older and is removed.
            var removed = (GridFsOrphanSweepReportDto)((OkObjectResult)await controller.SweepOrphans(TenantId,
                remove: true, gracePeriodMinutes: 60, cancellationToken: Ct)).Value!;
            removed.GracePeriodMinutes.Should().Be(60);
            removed.RemovedFiles.Should().Be(1);
            (await FileExistsAsync(orphan)).Should().BeFalse();

            // Below the engine minimum (10 minutes): refused, nothing runs.
            (await controller.SweepOrphans(TenantId, remove: true, gracePeriodMinutes: 5, cancellationToken: Ct))
                .Should().BeOfType<BadRequestObjectResult>();
        }
        finally
        {
            await CleanAsync(orphan);
        }
    }

    [Fact]
    public async Task UnauthorizedCallers_Get403_AndNothingIsDeleted()
    {
        var orphan = await UploadAsync(TimeSpan.FromDays(3));
        try
        {
            var callers = new[]
            {
                // admin role, but signed in to another tenant
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "x"), new Claim("tenant_id", "some-other-tenant"),
                    new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
                ], "IntegrationTests")),
                // system tenant user without the admin role
                FilesTestFixture.CreateUser("someone"),
                // no tenant claim at all
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "y")], "IntegrationTests")),
                new ClaimsPrincipal(new ClaimsIdentity())
            };

            foreach (var caller in callers)
            {
                (await CreateController(caller).SweepOrphans(TenantId, remove: true, cancellationToken: Ct))
                    .Should().BeOfType<ForbidResult>();
            }

            (await FileExistsAsync(orphan)).Should().BeTrue();
        }
        finally
        {
            await CleanAsync(orphan);
        }
    }

    [Fact]
    public async Task UnknownTenant_Gets404()
    {
        (await CreateController(SystemAdmin()).SweepOrphans("does-not-exist-" + Guid.NewGuid().ToString("N"),
                cancellationToken: Ct))
            .Should().BeOfType<NotFoundObjectResult>();
    }
}
