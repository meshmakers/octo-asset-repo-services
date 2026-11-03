using System.Net;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8228 — an authentication failure that GraphQL turns into a response error evicts the tenant's cached
///     MongoDB clients (once per window and tenant); other errors never do.
/// </summary>
public class GraphQlTenantPoolEvictionTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly TenantDatabaseUserHealer _healer = new();

    private static MongoCommandException CommandFailure(int code) =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))), "requires authentication",
            new BsonDocument(), new BsonDocument { { "code", code } });

    private static GraphQlUserContext UserContext(string tenantId)
    {
        var tenant = A.Fake<ITenantContext>();
        A.CallTo(() => tenant.TenantId).Returns(tenantId);
        return new GraphQlUserContext(null, tenant);
    }

    private Task<bool> Evict(Exception ex, object? userContext) =>
        GraphQlTenantPoolEviction.TryEvictAsync(ex, userContext, _systemContext, NullLogger.Instance, _healer);

    [Fact]
    public async Task Authentication_failure_evicts_the_tenants_clients_once_per_window()
    {
        (await Evict(CommandFailure(13), UserContext("meshtest"))).Should().BeTrue();
        (await Evict(CommandFailure(13), UserContext("meshtest"))).Should().BeFalse();

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("meshtest", null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Other_errors_do_not_evict()
    {
        (await Evict(new InvalidOperationException("boom"), UserContext("meshtest"))).Should().BeFalse();
        (await Evict(CommandFailure(11000), UserContext("meshtest"))).Should().BeFalse();

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Without_tenant_user_context_nothing_happens()
    {
        (await Evict(CommandFailure(13), null)).Should().BeFalse();
        (await GraphQlTenantPoolEviction.TryEvictAsync(CommandFailure(13), UserContext("meshtest"), null, null, _healer))
            .Should().BeFalse();
    }
}
