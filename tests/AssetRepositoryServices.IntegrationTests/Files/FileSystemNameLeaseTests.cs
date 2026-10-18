using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6885: the sibling-name lock holds across asset repository replicas. Two <see cref="FileSystemNameLocks" />
///     instances that share only the MongoDB lease store stand for two replicas (their in-process semaphores are
///     independent).
/// </summary>
[Collection(FilesMigrationCollection.Name)]
public class FileSystemNameLeaseTests(FilesMigrationTestFixture fixture, ITestOutputHelper output)
{
    private ITenantRepository Repository
    {
        get
        {
            fixture.OutputHelper = output;
            return fixture.GetSystemContext().GetTenantRepository();
        }
    }

    private string TenantId => fixture.GetSystemContext().TenantId;

    private IMongoCollection<BsonDocument> Leases => fixture.Collection(MongoFileSystemNameLeaseStore.CollectionName);

    private MongoFileSystemNameLeaseStore NewStore() =>
        new(fixture.GetService<ITenantMongoDatabaseProvider>(), NullLogger<MongoFileSystemNameLeaseStore>.Instance);

    private static string NewKey() =>
        FileSystemNameLocks.KeyOf(OctoObjectId.Parse(ObjectId.GenerateNewId().ToString()), "Reports");

    [Fact]
    public async Task SecondReplica_WaitsForTheFirstReplicasRelease()
    {
        var store = NewStore();
        var replicaA = new FileSystemNameLocks(store);
        var replicaB = new FileSystemNameLocks(store);
        using var sessionA = Repository.GetSession();
        using var sessionB = Repository.GetSession();
        var key = NewKey();

        await replicaA.AcquireAsync(sessionA, [key], tenantId: TenantId);
        (await Leases.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", key),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);

        var waiting = replicaB.AcquireAsync(sessionB, [key], tenantId: TenantId);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse("the other replica holds the name");

        await replicaA.ReleaseAsync(sessionA);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await replicaB.ReleaseAsync(sessionB);
        (await Leases.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", key),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task HeldByAnotherReplica_Answers409_AndLeavesNothingBehind()
    {
        var store = NewStore();
        var replicaA = new FileSystemNameLocks(store);
        var replicaB = new FileSystemNameLocks(store);
        using var sessionA = Repository.GetSession();
        using var sessionB = Repository.GetSession();
        var held = NewKey();
        var free = NewKey();
        await replicaA.AcquireAsync(sessionA, [held], tenantId: TenantId);

        var act = () => replicaB.AcquireAsync(sessionB, [free, held], TimeSpan.FromMilliseconds(300), TenantId);
        (await act.Should().ThrowAsync<FileSystemException>()).Which.StatusCode.Should().Be(409);

        replicaB.ActiveCount.Should().Be(0, "the in-process slots are given back");
        (await Leases.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", free),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0, "the lease of the key that was taken is given back");

        await replicaA.ReleaseAsync(sessionA);
    }

    [Fact]
    public async Task ExpiredLease_OfACrashedReplica_IsTakenOver()
    {
        var key = NewKey();
        await Leases.InsertOneAsync(new BsonDocument
        {
            { "_id", key },
            { "owner", "crashed-replica/0" },
            { "acquiredAt", DateTime.UtcNow.AddMinutes(-10) },
            { "expiresAt", DateTime.UtcNow.AddMinutes(-8) }
        }, cancellationToken: TestContext.Current.CancellationToken);

        var locks = new FileSystemNameLocks(NewStore());
        using var session = Repository.GetSession();
        await locks.AcquireAsync(session, [key], TimeSpan.FromSeconds(2), TenantId);

        var lease = await Leases.Find(Builders<BsonDocument>.Filter.Eq("_id", key))
            .SingleAsync(TestContext.Current.CancellationToken);
        lease["owner"].AsString.Should().NotBe("crashed-replica/0");
        lease["expiresAt"].ToUniversalTime().Should().BeAfter(DateTime.UtcNow);

        await locks.ReleaseAsync(session);
    }

    [Fact]
    public async Task ReleaseByAnotherOwner_DoesNotDeleteTheLease()
    {
        var store = NewStore();
        var key = NewKey();
        (await store.TryAcquireAsync(TenantId, key, "owner-1", TestContext.Current.CancellationToken)).Should().BeTrue();

        (await store.TryAcquireAsync(TenantId, key, "owner-2", TestContext.Current.CancellationToken)).Should().BeFalse();
        await store.ReleaseAsync(TenantId, key, "owner-2");
        (await store.TryAcquireAsync(TenantId, key, "owner-3", TestContext.Current.CancellationToken)).Should().BeFalse();

        await store.ReleaseAsync(TenantId, key, "owner-1");
        (await store.TryAcquireAsync(TenantId, key, "owner-3", TestContext.Current.CancellationToken)).Should().BeTrue();
        await store.ReleaseAsync(TenantId, key, "owner-3");
    }

    [Fact]
    public async Task WithoutTenantId_OnlyTheInProcessLockApplies()
    {
        var locks = new FileSystemNameLocks(NewStore());
        using var session = Repository.GetSession();
        var key = NewKey();

        await locks.AcquireAsync(session, [key]);

        (await Leases.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", key),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
        await locks.ReleaseAsync(session);
    }
}
