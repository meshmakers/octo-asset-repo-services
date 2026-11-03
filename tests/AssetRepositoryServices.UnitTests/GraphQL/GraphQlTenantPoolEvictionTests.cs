using System.Net;
using FakeItEasy;
using FluentAssertions;
using global::GraphQL;
using global::GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
///     AB#8228 round 2 — the resolvers of the asset repository catch their exceptions and map them with
///     <c>HandleException</c>; <c>UnhandledExceptionDelegate</c> never sees them. These tests run a real GraphQL.NET
///     execution through that mapping with the exception shape of a dropped tenant user (MongoCommandException,
///     code 13, "requires authentication").
/// </summary>
public class GraphQlHandleExceptionEvictionTests
{
    private static MongoCommandException AuthFailure() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))),
            "Command aggregate failed: Command aggregate requires authentication.",
            new BsonDocument(), new BsonDocument { { "code", 13 } });

    private static async Task<(global::GraphQL.ExecutionResult Result, ISystemContext SystemContext)> ExecuteAsync(
        string tenantId, Func<Exception> failure)
    {
        var systemContext = A.Fake<ISystemContext>();
        var services = new ServiceCollection().AddSingleton(systemContext).AddLogging().BuildServiceProvider();

        var query = new ObjectGraphType { Name = "Query" };
        query.Field<StringGraphType>("value").Resolve(ctx =>
        {
            try
            {
                throw failure();
            }
            catch (Exception e)
            {
                return ctx.HandleException(e);
            }
        });

        var tenant = A.Fake<ITenantContext>();
        A.CallTo(() => tenant.TenantId).Returns(tenantId);
        var result = await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = new Schema { Query = query };
            o.Query = "{ value }";
            o.RequestServices = services;
            o.UserContext = new GraphQlUserContext(null, tenant);
            o.CancellationToken = TestContext.Current.CancellationToken;
        });
        return (result, systemContext);
    }

    private static async Task<bool> WaitUntilEvicted(ISystemContext systemContext, string tenantId)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                A.CallTo(() => systemContext.InvalidateTenantRepositoryClientsAsync(tenantId, null, A<CancellationToken>._))
                    .MustHaveHappened();
                return true;
            }
            catch (FakeItEasy.ExpectationException)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        return false;
    }

    [Fact]
    public async Task Authentication_failure_mapped_by_HandleException_evicts_the_tenant_and_still_answers_with_the_error()
    {
        var tenantId = "tenant-" + Guid.NewGuid().ToString("N");

        var (result, systemContext) = await ExecuteAsync(tenantId, AuthFailure);

        result.Errors.Should().ContainSingle();
        (await WaitUntilEvicted(systemContext, tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task Other_failures_mapped_by_HandleException_do_not_evict()
    {
        var tenantId = "tenant-" + Guid.NewGuid().ToString("N");

        var (result, systemContext) = await ExecuteAsync(tenantId, () => new InvalidOperationException("boom"));

        result.Errors.Should().ContainSingle();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        A.CallTo(() => systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
