using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     The server-side time limit of runtime-entity queries in the asset repository host, read back from MongoDB's
///     profiler: a GraphQL runtime query reaches MongoDB with <c>maxTimeMS</c> from <c>RuntimeQuery:MaxTimeMs</c>
///     (default 120000), a changed value is used by the next query, and 0 sends no limit. That MongoDB stops a query
///     above the limit is proven in the engine; the mapping of the time-out to ASSET1008 / 504 by the unit tests of
///     <c>RuntimeQueryTimeoutError</c>.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public sealed class RuntimeQueryTimeLimitTests(GraphQlTestFixture fixture, ITestOutputHelper output)
{
    private const string PagedQuery =
        "{ runtime { assetRepositoryIntegrationTestCustomer(first: 2) { totalCount items { rtId } } } }";

    [Fact]
    public async Task ARuntimeQuery_ReachesMongoDbWithTheConfiguredMaxTimeMS()
    {
        var aggregates = await ProfiledAggregatesAsync(PagedQuery);

        LimitsOf(aggregates).Should().NotBeEmpty("page and count of the query carry the limit")
            .And.AllSatisfy(limit => limit.Should().Be(120_000));
    }

    [Fact]
    public async Task AChangedValue_IsUsedByTheNextQuery_AndZeroSendsNoLimit()
    {
        var options = fixture.Provider!.GetRequiredService<IOptionsMonitor<RuntimeQueryOptions>>().CurrentValue;
        var previous = options.MaxTimeMs;
        try
        {
            options.MaxTimeMs = 5_000;
            var changed = await ProfiledAggregatesAsync(PagedQuery);
            options.MaxTimeMs = 0;
            var off = await ProfiledAggregatesAsync(PagedQuery);

            LimitsOf(changed).Should().NotBeEmpty().And.AllSatisfy(limit => limit.Should().Be(5_000));
            LimitsOf(off).Should().BeEmpty();
        }
        finally
        {
            options.MaxTimeMs = previous;
        }
    }

    private static List<long> LimitsOf(IEnumerable<BsonDocument> aggregates) =>
        aggregates.Where(op => op["command"].AsBsonDocument.Contains("maxTimeMS"))
            .Select(op => op["command"]["maxTimeMS"].ToInt64())
            .ToList();

    private async Task<List<BsonDocument>> ProfiledAggregatesAsync(string query)
    {
        fixture.OutputHelper = output;
        using var client = new MongoClient(fixture.GetConnectionString());
        var database = client.GetDatabase(fixture.SystemDatabaseName);

        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        await database.DropCollectionAsync("system.profile");
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            var result = await fixture.ExecuteGraphQlAsync(query);
            result.Errors.Should().BeNullOrEmpty();
        }
        finally
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }

        return await database.GetCollection<BsonDocument>("system.profile")
            .Find(new BsonDocument("command.aggregate", new BsonDocument("$exists", true))).ToListAsync();
    }
}
