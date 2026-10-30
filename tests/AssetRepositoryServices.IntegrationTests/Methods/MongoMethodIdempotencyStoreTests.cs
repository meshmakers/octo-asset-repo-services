using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Methods;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Methods;

/// <summary>
///     AB#7824: the MongoDB idempotency store against a real MongoDB (private Testcontainer). Two store instances that
///     share only the database stand for two asset repository replicas.
/// </summary>
[Collection(FilesRawMongoCollection.Name)]
public class MongoMethodIdempotencyStoreTests(FilesRawMongoFixture fixture, ITestOutputHelper output)
{
    private const string Method = "System.Identity/User@ChangePassword";
    private const string RawKey = "raw-key-that-must-not-be-stored";
    private const string RawParameters = "{\"newPassword\":\"hunter2-raw-parameter\"}";

    // synthetic key material, never a real secret
    private static readonly string FingerprintSecret =
        Convert.ToBase64String(Enumerable.Repeat((byte)0x33, 32).ToArray());

    private string TenantId
    {
        get
        {
            fixture.OutputHelper = output;
            return fixture.GetSystemContext().TenantId;
        }
    }

    private IMongoCollection<BsonDocument> Records => fixture.Collection(MongoMethodIdempotencyStore.CollectionName);

    private MongoMethodIdempotencyStore NewStore(TimeProvider? time = null, TimeSpan? ttl = null,
        ITenantMongoDatabaseProvider? provider = null) =>
        new(provider ?? fixture.GetService<ITenantMongoDatabaseProvider>(), Lifetime(ttl), time ?? TimeProvider.System,
            NullLogger<MongoMethodIdempotencyStore>.Instance);

    private static CkMethodIdempotencyLifetime Lifetime(TimeSpan? ttl) =>
        new(Options.Create(new CkMethodIdempotencyOptions { Ttl = ttl }),
            NullLogger<CkMethodIdempotencyLifetime>.Instance);

    private CkMethodIdempotencyKey NewKey(string? rawKey = null) =>
        CkMethodIdempotencyKey.Create(TenantId, Method, "user-1", rawKey ?? $"key-{Guid.NewGuid():N}");

    private static CkMethodIdempotencyClaimRequest Claim(CkMethodIdempotencyKey key, Guid? invocation = null,
        byte[]? fingerprint = null) =>
        new(key, invocation ?? Guid.NewGuid(), DateTime.UtcNow.AddSeconds(60), fingerprint ?? [1, 2, 3, 4],
            "k-current");

    // ---------------------------------------------------------------- atomic claim

    [Fact]
    public async Task FirstClaim_Wins_AndASecondClaimSeesTheLiveRecord()
    {
        var store = NewStore();
        var key = NewKey();
        var first = Claim(key);

        var a = await store.ClaimAsync(first, TestContext.Current.CancellationToken);
        var b = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);

        a.Claimed.Should().BeTrue();
        a.Record.State.Should().Be(CkMethodIdempotencyState.InProgress);
        b.Claimed.Should().BeFalse();
        b.Record.FirstInvocationId.Should().Be(first.FirstInvocationId);
        b.Record.State.Should().Be(CkMethodIdempotencyState.InProgress);
        b.Record.Fingerprint.ToArray().Should().Equal(1, 2, 3, 4);
        b.Record.FingerprintKeyId.Should().Be("k-current");
    }

    [Fact]
    public async Task TwoReplicas_FiftyConcurrentClaimsOfOneKey_ExactlyOneWins()
    {
        var replicaA = NewStore();
        var replicaB = NewStore();
        var key = NewKey();

        var claims = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => (i % 2 == 0 ? replicaA : replicaB).ClaimAsync(Claim(key),
                TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        claims.Count(c => c.Claimed).Should().Be(1);
        var winner = claims.Single(c => c.Claimed).Record.FirstInvocationId;
        claims.Where(c => !c.Claimed).Should().OnlyContain(c => c.Record.FirstInvocationId == winner);
        (await Records.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", key.Id),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task TwoReplicas_ShareOneKey_OnlyOneHandlerExecutes_ThroughTheRealFilter()
    {
        var replicaA = NewPipeline(NewStore());
        var replicaB = NewPipeline(NewStore());
        var executions = 0;
        var rawKey = $"race-{Guid.NewGuid():N}";

        async Task<CkMethodResponse> Call(ICkMethodInvocationPipeline pipeline)
        {
            using var _ = CkMethodPrincipalScope.Begin(new CkMethodPrincipal("user-1", [], []));
            return await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, Request(rawKey), async r =>
            {
                Interlocked.Increment(ref executions);
                await Task.Delay(300);
                return CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "\"done\"");
            }, TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => Call(i % 2 == 0 ? replicaA : replicaB), TestContext.Current.CancellationToken)));

        executions.Should().Be(1, "the handler runs once, whichever replica the retry reaches");
        foreach (var response in responses)
        {
            if (response.Success)
            {
                response.ResultJson.Should().Be("\"done\"");
            }
            else
            {
                response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodRequestInProgress);
            }
        }

        responses.Count(r => r.Success).Should().BeGreaterThanOrEqualTo(1);

        // after the first call settled, a repeat on the other replica is replayed from the shared record
        var replay = await Call(replicaB);
        replay.Success.Should().BeTrue();
        replay.ResultJson.Should().Be("\"done\"");
        executions.Should().Be(1);
    }

    // ---------------------------------------------------------------- complete and release

    [Fact]
    public async Task Complete_StoresTheEnvelope_AndAnotherReplicaReplaysItExactly()
    {
        var replicaA = NewStore();
        var replicaB = NewStore();
        var key = NewKey();
        var invocation = Guid.NewGuid();
        (await replicaA.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken)).Claimed.Should().BeTrue();
        var envelope = CkMethodResponse.Failed(invocation, "handler-1", new CkMethodError
        {
            Code = "MY_DECLARED_ERROR",
            Parameters = new Dictionary<string, string> { ["field"] = "name" },
            Details = [new CkMethodErrorDetail { Code = "d1", Parameters = new Dictionary<string, string> { ["n"] = "2" } }]
        });

        (await replicaA.CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Completed, envelope,
            TestContext.Current.CancellationToken)).Should().BeTrue();

        var seen = await replicaB.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        seen.Claimed.Should().BeFalse();
        seen.Record.State.Should().Be(CkMethodIdempotencyState.Completed);
        seen.Record.Response.Should().BeEquivalentTo(envelope);
    }

    [Fact]
    public async Task Complete_AsUnknown_BurnsTheKey_WithoutAResponse()
    {
        var store = NewStore();
        var key = NewKey();
        var invocation = Guid.NewGuid();
        await store.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken);

        (await store.CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Unknown, null,
            TestContext.Current.CancellationToken)).Should().BeTrue();

        var seen = await NewStore().ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        seen.Claimed.Should().BeFalse();
        seen.Record.State.Should().Be(CkMethodIdempotencyState.Unknown);
        seen.Record.Response.Should().BeNull();
    }

    [Fact]
    public async Task Complete_ByANonOwner_OrTwice_IsRefused()
    {
        var store = NewStore();
        var key = NewKey();
        var invocation = Guid.NewGuid();
        await store.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken);
        var envelope = CkMethodResponse.Succeeded(invocation, "h", "1");

        // another process does not own the claim: it never remembered it
        (await NewStore().CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Completed, envelope,
            TestContext.Current.CancellationToken)).Should().BeFalse();
        // right process, wrong invocation id
        (await store.CompleteAsync(key.Id, Guid.NewGuid(), CkMethodIdempotencyState.Completed, envelope,
            TestContext.Current.CancellationToken)).Should().BeFalse();

        var second = NewKey();
        await store.ClaimAsync(Claim(second, invocation), TestContext.Current.CancellationToken);
        (await store.CompleteAsync(second.Id, invocation, CkMethodIdempotencyState.Completed, envelope,
            TestContext.Current.CancellationToken)).Should().BeTrue();
        (await store.CompleteAsync(second.Id, invocation, CkMethodIdempotencyState.Completed, envelope,
            TestContext.Current.CancellationToken)).Should().BeFalse("a settled record is not in progress any more");
    }

    [Fact]
    public async Task Complete_NeverExtendsTheLifetime()
    {
        var store = NewStore();
        var key = NewKey();
        var invocation = Guid.NewGuid();
        await store.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken);
        var before = (await Records.Find(Builders<BsonDocument>.Filter.Eq("_id", key.Id))
            .FirstAsync(TestContext.Current.CancellationToken))["expiresAt"];

        await store.CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Completed,
            CkMethodResponse.Succeeded(invocation, "h"), TestContext.Current.CancellationToken);

        (await Records.Find(Builders<BsonDocument>.Filter.Eq("_id", key.Id))
            .FirstAsync(TestContext.Current.CancellationToken))["expiresAt"].Should().Be(before);
    }

    [Fact]
    public async Task Release_FreesTheKeyAtOnce_OnlyForTheOwnerWhileInProgress()
    {
        var store = NewStore();
        var key = NewKey();
        var invocation = Guid.NewGuid();
        await store.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken);

        (await store.ReleaseAsync(key.Id, Guid.NewGuid(), TestContext.Current.CancellationToken)).Should().BeFalse();
        // the wrong-invocation attempt forgot the claim; claim again to release as the owner
        var again = NewKey();
        var owner = Guid.NewGuid();
        await store.ClaimAsync(Claim(again, owner), TestContext.Current.CancellationToken);
        (await store.ReleaseAsync(again.Id, owner, TestContext.Current.CancellationToken)).Should().BeTrue();

        (await NewStore().ClaimAsync(Claim(again), TestContext.Current.CancellationToken)).Claimed.Should()
            .BeTrue("a released claim may be taken again at once");
    }

    // ---------------------------------------------------------------- expiry

    [Fact]
    public async Task ExpiredRecord_IsTakenOver_AlthoughTheTtlMonitorHasNotRemovedIt()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = NewStore(time, TimeSpan.FromHours(1));
        var key = NewKey();
        var first = Guid.NewGuid();
        (await store.ClaimAsync(Claim(key, first), TestContext.Current.CancellationToken)).Claimed.Should().BeTrue();

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        // the database still holds the document (the monitor only runs about once a minute and the real clock has not moved)
        (await Records.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", key.Id),
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
        var second = Guid.NewGuid();
        var takeover = await NewStore(time, TimeSpan.FromHours(1))
            .ClaimAsync(Claim(key, second), TestContext.Current.CancellationToken);

        takeover.Claimed.Should().BeTrue();
        takeover.Record.FirstInvocationId.Should().Be(second);
        var live = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        live.Claimed.Should().BeFalse();
        live.Record.FirstInvocationId.Should().Be(second);
    }

    [Fact]
    public async Task ExpiredRecord_ThirtyConcurrentTakers_ExactlyOneWins()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var key = NewKey();
        await NewStore(time, TimeSpan.FromHours(1)).ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(2));
        var replicaA = NewStore(time, TimeSpan.FromHours(1));
        var replicaB = NewStore(time, TimeSpan.FromHours(1));

        var claims = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Task.Run(() => (i % 2 == 0 ? replicaA : replicaB).ClaimAsync(Claim(key),
                TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        claims.Count(c => c.Claimed).Should().Be(1);
    }

    [Fact]
    public async Task Complete_OfAnExpiredClaim_IsRefused()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = NewStore(time, TimeSpan.FromHours(1));
        var key = NewKey();
        var invocation = Guid.NewGuid();
        await store.ClaimAsync(Claim(key, invocation), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(2));

        (await store.CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Completed,
            CkMethodResponse.Succeeded(invocation, "h"), TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task TtlIndex_ExpiresAtWithZeroSeconds_IsCreatedOnFirstUse()
    {
        await NewStore().ClaimAsync(Claim(NewKey()), TestContext.Current.CancellationToken);

        var indexes = await (await Records.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        var ttl = indexes.Single(i => i["name"] == MongoMethodIdempotencyStore.TtlIndexName);
        ttl["key"].AsBsonDocument.Names.Should().Equal("expiresAt");
        ttl["expireAfterSeconds"].ToInt64().Should().Be(0);
    }

    [Fact]
    public async Task ChangedTtl_AppliesToNewClaims_WithoutTouchingTheIndex()
    {
        var oneHour = NewKey();
        var twoDays = NewKey();
        await NewStore(ttl: TimeSpan.FromHours(1)).ClaimAsync(Claim(oneHour), TestContext.Current.CancellationToken);
        var indexBefore = (await (await Records.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken)).Single(i => i["name"] == MongoMethodIdempotencyStore.TtlIndexName);

        await NewStore(ttl: TimeSpan.FromDays(2)).ClaimAsync(Claim(twoDays), TestContext.Current.CancellationToken);

        var a = await Records.Find(Builders<BsonDocument>.Filter.Eq("_id", oneHour.Id)).FirstAsync(TestContext.Current.CancellationToken);
        var b = await Records.Find(Builders<BsonDocument>.Filter.Eq("_id", twoDays.Id)).FirstAsync(TestContext.Current.CancellationToken);
        (b["expiresAt"].ToUniversalTime() - a["expiresAt"].ToUniversalTime()).Should()
            .BeCloseTo(TimeSpan.FromHours(47), TimeSpan.FromMinutes(1));
        var indexAfter = (await (await Records.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken)).Single(i => i["name"] == MongoMethodIdempotencyStore.TtlIndexName);
        indexAfter.ToJson().Should().Be(indexBefore.ToJson());
    }

    // ---------------------------------------------------------------- what is stored

    [Fact]
    public async Task StoredDocument_HoldsNeitherTheRawKeyNorRawParameters_NorTheTenantOrSubject()
    {
        var pipeline = NewPipeline(NewStore());
        var key = CkMethodIdempotencyKey.Create(TenantId, Method, "user-1", RawKey);
        using (CkMethodPrincipalScope.Begin(new CkMethodPrincipal("user-1", [], [])))
        {
            var response = await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, Request(RawKey, RawParameters),
                r => Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "\"ok\"")),
                TestContext.Current.CancellationToken);
            response.Success.Should().BeTrue();
        }

        var stored = await Records.Find(Builders<BsonDocument>.Filter.Eq("_id", key.Id))
            .FirstAsync(TestContext.Current.CancellationToken);
        var text = stored.ToJson();
        text.Should().NotContain(RawKey).And.NotContain("hunter2").And.NotContain("newPassword");
        text.Should().NotContain(TenantId).And.NotContain("user-1");
        stored["_id"].AsString.Should().MatchRegex("^[0-9a-f]{64}$");
        stored["fingerprint"].AsBsonBinaryData.Bytes.Should().HaveCount(32, "the keyed HMAC is stored as given, not the parameters");
        stored["fingerprintKeyId"].AsString.Should().Be("k-current");
        stored["state"].ToInt32().Should().Be((int)CkMethodIdempotencyState.Completed);
        stored.Names.Should().BeEquivalentTo("_id", "state", "methodId", "firstInvocationId", "fingerprint",
            "fingerprintKeyId", "claimedAt", "leaseUntil", "expiresAt", "response");
    }

    [Fact]
    public async Task ResultSensitiveMethod_RefusesTheKey_AndNothingIsStored()
    {
        var collectionBefore = await Records.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var pipeline = NewPipeline(NewStore(), resultSensitive: true);
        var executed = false;
        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(new CkMethodPrincipal("user-1", [], [])))
        {
            response = await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, Request($"sens-{Guid.NewGuid():N}"),
                r =>
                {
                    executed = true;
                    return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "\"secret\""));
                }, TestContext.Current.CancellationToken);
        }

        response.Success.Should().BeFalse();
        response.Error!.Parameters[CkMethodErrorParameters.Reason].Should().Be("result-sensitive");
        executed.Should().BeFalse();
        (await Records.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(collectionBefore);
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task MongoDbUnreachable_ClaimThrowsStoreException_AndTheFilterAnswersStoreUnavailable_UnkeyedCallsAreNotAffected()
    {
        var store = NewStore(provider: new UnreachableProvider());
        var key = NewKey();

        var act = () => store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<CkMethodIdempotencyStoreException>();

        var pipeline = NewPipeline(store);
        var executions = 0;
        using var _ = CkMethodPrincipalScope.Begin(new CkMethodPrincipal("user-1", [], []));
        var keyed = await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, Request("any-key"), r =>
        {
            executions++;
            return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h"));
        }, TestContext.Current.CancellationToken);
        keyed.Success.Should().BeFalse();
        keyed.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerNotAvailable);
        keyed.Error.Parameters[CkMethodErrorParameters.Reason].Should().Be("idempotency-store-unavailable");
        executions.Should().Be(0, "fail closed: the method is not executed");

        var unkeyed = await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, Request(null), r =>
        {
            executions++;
            return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h"));
        }, TestContext.Current.CancellationToken);
        unkeyed.Success.Should().BeTrue();
        executions.Should().Be(1);
    }

    [Fact]
    public async Task MissingAdminCredentials_AndUnknownTenant_FailClosed()
    {
        var noCredentials = NewStore(provider: new ThrowingProvider(new InvalidOperationException("no admin credentials")));
        var act = () => noCredentials.ClaimAsync(Claim(NewKey()), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<CkMethodIdempotencyStoreException>();

        var unknownTenant = NewStore(provider: new NullDatabaseProvider());
        var act2 = () => unknownTenant.ClaimAsync(Claim(NewKey()), TestContext.Current.CancellationToken);
        await act2.Should().ThrowAsync<CkMethodIdempotencyStoreException>();
    }

    [Fact]
    public async Task CallerCancellation_IsNotAStoreFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => NewStore().ClaimAsync(Claim(NewKey()), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- benchmark (opt-in)

    [Fact]
    public async Task Benchmark_KeyedCall_ClaimAndComplete_LatencyIsReported()
    {
        var store = NewStore();
        const int warmup = 50;
        const int samples = 500;
        var claim = new List<double>(samples);
        var complete = new List<double>(samples);
        for (var i = 0; i < warmup + samples; i++)
        {
            var key = NewKey();
            var invocation = Guid.NewGuid();
            var t0 = Stopwatch.GetTimestamp();
            await store.ClaimAsync(Claim(key, invocation, new byte[32]), TestContext.Current.CancellationToken);
            var t1 = Stopwatch.GetTimestamp();
            await store.CompleteAsync(key.Id, invocation, CkMethodIdempotencyState.Completed,
                CkMethodResponse.Succeeded(invocation, "handler-1", "\"done\""), TestContext.Current.CancellationToken);
            var t2 = Stopwatch.GetTimestamp();
            if (i >= warmup)
            {
                claim.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                complete.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            }
        }

        var both = claim.Zip(complete, (a, b) => a + b).ToList();
        output.WriteLine(
            $"BENCHMARK n={samples} claim p50={P(claim, 50):F2} p95={P(claim, 95):F2} p99={P(claim, 99):F2} ms; " +
            $"complete p50={P(complete, 50):F2} p95={P(complete, 95):F2} p99={P(complete, 99):F2} ms; " +
            $"claim+complete p50={P(both, 50):F2} p95={P(both, 95):F2} p99={P(both, 99):F2} ms");
        both.Should().NotBeEmpty();
    }

    private static double P(List<double> values, int percentile)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)];
    }

    // ---------------------------------------------------------------- rig

    private CkMethodRequest Request(string? idempotencyKey, string parametersJson = "{}") =>
        new(TenantId)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = Method,
            CkTypeId = "System.Identity/User",
            TargetRtId = "65f0c0ffee",
            ParametersJson = parametersJson,
            Caller = new CkMethodCaller { SubjectId = "user-1" },
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30),
            IdempotencyKey = idempotencyKey
        };

    private static ICkMethodInvocationPipeline NewPipeline(ICkMethodIdempotencyStore store, bool resultSensitive = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CkMethodFingerprintOptions.SectionName}:Current:Id"] = "k-current",
            [$"{CkMethodFingerprintOptions.SectionName}:Current:Secret"] = FingerprintSecret
        }).Build());
        services.AddSingleton<ICkMethodSensitivityResolver>(new Sensitivity(resultSensitive));
        services.AddCkMethodIdempotency();
        services.AddSingleton(store);
        return services.BuildServiceProvider().GetRequiredService<ICkMethodInvocationPipeline>();
    }

    private sealed class Sensitivity(bool resultSensitive) : ICkMethodSensitivityResolver
    {
        public IReadOnlySet<string>? GetSensitiveParameters(string tenantId, string ckTypeId, string qualifiedMethodId) =>
            new HashSet<string>();

        public bool IsResultSensitive(string tenantId, string ckTypeId, string qualifiedMethodId) => resultSensitive;
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ThrowingProvider(Exception exception) : ITenantMongoDatabaseProvider
    {
        public Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId) => throw exception;
    }

    private sealed class NullDatabaseProvider : ITenantMongoDatabaseProvider
    {
        public Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId) => Task.FromResult<IMongoDatabase?>(null);
    }

    private sealed class UnreachableProvider : ITenantMongoDatabaseProvider
    {
        private static readonly IMongoDatabase Database = new MongoClient(
            "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500&connectTimeoutMS=500").GetDatabase("nowhere");

        public Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId) => Task.FromResult<IMongoDatabase?>(Database);
    }
}
