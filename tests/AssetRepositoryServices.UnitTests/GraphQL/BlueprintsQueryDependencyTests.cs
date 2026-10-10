using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#6306: listing / searching blueprints must not fetch the manifest of every entry (one HTTP GET per
///     blueprint on GitHub catalogs) unless the client selects <c>blueprintDependencies</c> or
///     <c>ckModelDependencies</c>; when selected, the fetches run concurrently but bounded.
/// </summary>
public class BlueprintsQueryDependencyTests
{
    private readonly IBlueprintCatalogManager _catalogManager = A.Fake<IBlueprintCatalogManager>();

    private static BlueprintCatalogResultItem Item(string id) =>
        new() { BlueprintId = new BlueprintId(id), Description = "desc", CatalogName = "TestCatalog" };

    private void SetupListing(int count)
    {
        var items = Enumerable.Range(1, count).Select(i => Item($"Bp{i}-1.0.0")).ToList();
        A.CallTo(() => _catalogManager.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new BlueprintListResult { Items = items, TotalCount = count });
        A.CallTo(() => _catalogManager.SearchAsync(A<string>._, A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new BlueprintSearchResult { Items = items, TotalCount = count });
    }

    private async Task<JsonElement> ExecuteAsync(string query)
    {
        var schema = new Schema { Query = new BlueprintsQuery(NullLogger<BlueprintsQuery>.Instance) };
        var services = new ServiceCollection().AddSingleton(_catalogManager).BuildServiceProvider();

        var result = await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = schema;
            o.Query = query;
            o.RequestServices = services;
        });

        result.Errors.Should().BeNull();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement.GetProperty("data").Clone();
    }

    private void AssertNoManifestFetch() =>
        A.CallTo(() => _catalogManager.TryGetAsync(A<BlueprintId>._, A<OperationResult>._, A<object?>._,
            A<CancellationToken?>._)).MustNotHaveHappened();

    [Fact]
    public async Task List_WithoutDependencySelection_DoesNotFetchManifests()
    {
        SetupListing(5);

        var data = await ExecuteAsync("{ list(take: 5) { totalCount items { id name version description catalogName } } }");

        data.GetProperty("list").GetProperty("items").GetArrayLength().Should().Be(5);
        AssertNoManifestFetch();
    }

    [Fact]
    public async Task Search_WithoutDependencySelection_DoesNotFetchManifests()
    {
        SetupListing(3);

        await ExecuteAsync("{ search(query: \"Bp\") { items { id name } } }");

        AssertNoManifestFetch();
    }

    [Fact]
    public async Task List_WithDependencySelection_ResolvesDependenciesFromManifest()
    {
        SetupListing(2);
        A.CallTo(() => _catalogManager.TryGetAsync(A<BlueprintId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily(() => new BlueprintMetaRootDto
            {
                BlueprintDependencies = [new BlueprintIdVersionRange("Base-[1.0.0,)")],
                CkModelDependencies = [new CkModelIdVersionRange("Meshmakers.Accounting-[1.24.0,2.0)")]
            });

        var data = await ExecuteAsync("{ list { items { id blueprintDependencies ckModelDependencies } } }");

        var first = data.GetProperty("list").GetProperty("items")[0];
        first.GetProperty("blueprintDependencies").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Base-[1.0.0,)");
        first.GetProperty("ckModelDependencies").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Meshmakers.Accounting-[1.24.0,2.0)");
        // Both dependency fields of one item share a single manifest fetch.
        A.CallTo(() => _catalogManager.TryGetAsync(A<BlueprintId>._, A<OperationResult>._, A<object?>._,
            A<CancellationToken?>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task List_WithDependencySelection_UnknownManifest_YieldsEmptyLists()
    {
        SetupListing(1);
        A.CallTo(() => _catalogManager.TryGetAsync(A<BlueprintId>._, A<OperationResult>._, A<object?>._,
            A<CancellationToken?>._)).Returns((BlueprintMetaRootDto?)null);

        var data = await ExecuteAsync("{ list { items { blueprintDependencies ckModelDependencies } } }");

        var item = data.GetProperty("list").GetProperty("items")[0];
        item.GetProperty("blueprintDependencies").GetArrayLength().Should().Be(0);
        item.GetProperty("ckModelDependencies").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_WithDependencySelection_FetchesConcurrentlyButBounded()
    {
        const int count = 40;
        SetupListing(count);
        var inFlight = 0;
        var peak = 0;
        A.CallTo(() => _catalogManager.TryGetAsync(A<BlueprintId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily(async () =>
            {
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                {
                }

                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return (BlueprintMetaRootDto?)new BlueprintMetaRootDto();
            });

        await ExecuteAsync("{ list(take: 40) { items { blueprintDependencies } } }");

        peak.Should().BeGreaterThan(1, "manifests must be fetched concurrently");
        peak.Should().BeLessThanOrEqualTo(BlueprintsQuery.MaxConcurrentManifestFetches);
    }
}
