using System.Net;
using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Observability;

/// <summary>AB#8228 round 3 — the CK model sweep (a background reader per tenant) evicts stale pools on error 13.</summary>
public class CkModelObservabilitySweepEvictionTests
{
    private static MongoCommandException Code(int code) =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))),
            "Command aggregate failed: Command aggregate requires authentication.",
            new BsonDocument(), new BsonDocument { { "code", code } });

    [Fact]
    public async Task An_authentication_failure_of_the_sweep_evicts_the_tenant_once_per_window()
    {
        var systemContext = A.Fake<ISystemContext>();
        var healer = new TenantDatabaseUserHealer();

        Assert.True(await CkModelObservabilitySweepService.EvictOnAuthenticationFailureAsync(systemContext, "tmpqa", Code(13),
            NullLogger.Instance, healer));
        Assert.False(await CkModelObservabilitySweepService.EvictOnAuthenticationFailureAsync(systemContext, "tmpqa", Code(13),
            NullLogger.Instance, healer));

        A.CallTo(() => systemContext.InvalidateTenantRepositoryClientsAsync("tmpqa", null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Other_sweep_failures_do_not_evict()
    {
        var systemContext = A.Fake<ISystemContext>();

        Assert.False(await CkModelObservabilitySweepService.EvictOnAuthenticationFailureAsync(systemContext, "tmpqa",
            new TimeoutException(), NullLogger.Instance, new TenantDatabaseUserHealer()));

        A.CallTo(() => systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
