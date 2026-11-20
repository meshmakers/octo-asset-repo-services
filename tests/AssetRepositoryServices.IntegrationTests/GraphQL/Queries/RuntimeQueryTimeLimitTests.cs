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
        var operations = await ProfiledOperationsAsync(PagedQuery);

        AssertLimit(operations, 120_000);
    }

    [Fact]
    public async Task AChangedValue_IsUsedByTheNextQuery_AndZeroSendsNoLimit()
    {
        var options = fixture.Provider!.GetRequiredService<IOptionsMonitor<RuntimeQueryOptions>>().CurrentValue;
        var previous = options.MaxTimeMs;
        try
        {
            options.MaxTimeMs = 5_000;
            var changed = await ProfiledOperationsAsync(PagedQuery);
            options.MaxTimeMs = 0;
            var off = await ProfiledOperationsAsync(PagedQuery);

            AssertLimit(changed, 5_000);
            AssertLimit(off, null);
        }
        finally
        {
            options.MaxTimeMs = previous;
        }
    }

    /// <summary>
    ///     The profiler captured the page and the count of the query (the count as a <c>count</c> command or as an
    ///     aggregate ending in <c>$count</c> / <c>$group</c>, which is how the driver sends <c>countDocuments</c>), and
    ///     each of them carries <paramref name="expected" /> as <c>maxTimeMS</c> - or none when it is null.
    /// </summary>
    private static void AssertLimit(List<BsonDocument> operations, long? expected)
    {
        var counts = operations.Where(IsCount).ToList();
        var countNamespaces = counts.Select(op => op["ns"].AsString).ToHashSet();
        // Page operations: the other aggregates on the collection the count read.
        var pages = operations.Where(op => !IsCount(op) && countNamespaces.Contains(op["ns"].AsString)).ToList();

        counts.Should().NotBeEmpty("the query asks for totalCount");
        pages.Should().NotBeEmpty("the query asks for a page of items");
        foreach (var op in pages.Concat(counts))
        {
            var command = op["command"].AsBsonDocument;
            if (expected is { } limit)
            {
                command.Contains("maxTimeMS").Should().BeTrue($"every operation carries the limit: {command}");
                command["maxTimeMS"].ToInt64().Should().Be(limit);
            }
            else
            {
                command.Contains("maxTimeMS").Should().BeFalse($"no operation carries a limit: {command}");
            }
        }
    }

    private static bool IsCount(BsonDocument op)
    {
        var command = op["command"].AsBsonDocument;
        if (command.Contains("count"))
        {
            return true;
        }

        if (!command.TryGetValue("pipeline", out var pipeline) || !pipeline.IsBsonArray || pipeline.AsBsonArray.Count == 0)
        {
            return false;
        }

        var last = pipeline.AsBsonArray.Last().AsBsonDocument;
        return last.Contains("$count") || last.Contains("$group");
    }

    private async Task<List<BsonDocument>> ProfiledOperationsAsync(string query)
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

        var filter = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("command.aggregate", new BsonDocument("$exists", true)),
            new BsonDocument("command.count", new BsonDocument("$exists", true))
        });
        return await database.GetCollection<BsonDocument>("system.profile").Find(filter).ToListAsync();
    }
}
