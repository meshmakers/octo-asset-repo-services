using System.Diagnostics;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.ByInterface;

/// <summary>
///     AB#6932: the budget of the interface query is NOT re-implemented in the GraphQL layer (decision AB#6930, engine
///     method AB#7019). This test shows that the GraphQL path adds no measurable cost beyond the engine call and that the
///     end-to-end first page stays inside the decided budget. Same non-flaky style as the dispatcher performance test:
///     warm-up, median / p95 of repeated runs, ratios and named-constant ceilings, an unindexed sort that is printed and
///     never asserted.
///     <para>
///         <b>Size.</b> Default 100 000 entities over 30 collection roots; the 1M run is opt-in with
///         <c>OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000</c>. Exclude from a quick loop with
///         <c>--filter "Category!=Performance"</c>. The measured numbers go to the test output and into the asset-repo
///         <c>CLAUDE.md</c>.
///     </para>
/// </summary>
[Collection(ByInterfaceWideCollection.Name)]
public class ByInterfacePerformanceTests
{
    // ---- Budget of the decision AB#6930 (2026-10-19, Gerald, option A), as named constants ---------------------------

    /// <summary>(b) Several collections, sort field indexed: end-to-end first-page p95 at most 100 ms (30 collections, 1M entities).</summary>
    private const double IndexedMultiCollectionFirstPageP95CeilingMs = 100;

    /// <summary>(a) One collection: at most 1.25x of a type query that returns the same rows - here both measured through GraphQL.</summary>
    private const double SameCollectionRatioCeiling = 1.25;

    /// <summary>Timer noise allowance of a ratio check: at a median of a few milliseconds a ratio is meaningless.</summary>
    private const double AbsoluteSlackMs = 10;

    /// <summary>
    ///     The GraphQL layer (parse, validate, resolve 50 items with a fragment, serialize) may add this much to the
    ///     median of the bare engine call. Not part of the decision: "no measurable cost beyond the engine call" made
    ///     operational with a generous ceiling.
    /// </summary>
    private const double GraphQlOverheadCeilingMs = 25;

    private const string EntityCountVariable = "OCTO_TEST_INTERFACE_QUERY_ENTITIES";
    private const int DefaultEntityCount = 100_000;
    private const int WarmUpRuns = 3;
    private const int MeasuredRuns = 20;
    private const int PageSize = 50;
    private const int Seed = 6932;

    private readonly ByInterfaceWideGraphQlTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ByInterfacePerformanceTests(ByInterfaceWideGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task IndexedSort_FirstPage_GraphQlAddsNoMeasurableCostBeyondTheEngine_AndStaysInTheBudget()
    {
        var entityCount = int.TryParse(Environment.GetEnvironmentVariable(EntityCountVariable), out var configured) &&
                          configured > 0
            ? configured
            : DefaultEntityCount;

        var loadWatch = Stopwatch.StartNew();
        await LoadAsync(entityCount);
        Report($"loaded {entityCount:N0} entities over {ByInterfaceWideGraphQlTestFixture.PerfRoots} collections " +
               $"in {loadWatch.Elapsed.TotalSeconds:F1}s; indexes of Perf/T01: " +
               string.Join(" | ", await _fixture.IndexKeysAsync("Perf", 1)));

        var repository = _fixture.GetSystemContext().GetTenantRepository();
        var nameAscending = RtEntityQueryOptions.Create().SortOrder("name", SortOrders.Ascending);
        var named = new RtCkId<CkInterfaceId>("Perf/Named-1");

        // The same first page, once through the bare engine method and once through GraphQL (parse, validate,
        // resolve, items with an interface fragment, serialization).
        var engine = await TimeAsync(async () =>
        {
            using var session = await repository.GetSessionAsync();
            var result = await repository.GetRtEntitiesByInterfaceAsync(session, named, nameAscending, 0, PageSize);
            result.Items.Should().HaveCount(PageSize);
        });
        var graphQl = await TimeAsync(() => RunPageAsync("Named", "name", 0, PageSize, count: false));
        var graphQlDeep = await TimeAsync(() => RunPageAsync("Named", "name", 450, PageSize, count: false));
        var graphQlCounted = await TimeAsync(() => RunPageAsync("Named", "name", 0, PageSize, count: true));
        var unindexed = await TimeAsync(() => RunPageAsync("Named", "rank", 0, PageSize, count: false), 2, 3);

        Report($"[{entityCount:N0} entities, {ByInterfaceWideGraphQlTestFixture.PerfRoots} collections] " +
               $"engine first page: median {engine.Median:F1} ms, p95 {engine.P95:F1} ms | " +
               $"GraphQL first page: median {graphQl.Median:F1} ms, p95 {graphQl.P95:F1} ms | " +
               $"GraphQL skip 450: median {graphQlDeep.Median:F1} ms, p95 {graphQlDeep.P95:F1} ms | " +
               $"GraphQL with count: median {graphQlCounted.Median:F1} ms, p95 {graphQlCounted.P95:F1} ms | " +
               $"GraphQL UNINDEXED sort (rank): median {unindexed.Median:F0} ms, p95 {unindexed.P95:F0} ms " +
               "(printed, not asserted: O(matching rows))");

        graphQl.Median.Should().BeLessThanOrEqualTo(engine.Median + GraphQlOverheadCeilingMs,
            "the GraphQL layer is a thin layer over the engine call (AB#6932)");
        graphQl.P95.Should().BeLessThanOrEqualTo(IndexedMultiCollectionFirstPageP95CeilingMs,
            "end-to-end indexed first page p95 stays inside the budget of the decision AB#6930");

        // (a) the implementers live in ONE collection: against the type query of the same collection, both through GraphQL
        var viaInterface = await TimeAsync(() => RunPageAsync("Solo", "name", 0, PageSize, count: true));
        var viaType = await TimeAsync(() => RunTypePageAsync(PageSize));
        var ceiling = Math.Max(SameCollectionRatioCeiling * viaType.Median, viaType.Median + AbsoluteSlackMs);
        Report($"one collection, indexed sort, with count: interface median {viaInterface.Median:F1} ms vs type query " +
               $"{viaType.Median:F1} ms (ratio {viaInterface.Median / Math.Max(viaType.Median, 0.001):F2}, ceiling {ceiling:F1} ms)");
        viaInterface.Median.Should().BeLessThanOrEqualTo(ceiling);
    }

    [Fact]
    public async Task MoreThanSixtyFourCollectionRoots_IsRefusedWithTheStableErrorNotAServerError()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            query { runtime { byInterface(ckInterfaceId: "Wide/Named-1", take: 5) { totalCount } } }
            """);

        result.Errors.Should().ContainSingle();
        var error = result.Errors![0];
        error.Code.Should().Be("INTERFACE_QUERY_REFUSED");
        error.Extensions!["messageNumber"].Should().Be(7019);
        error.Extensions["reason"].Should().Be("TooManyCollectionRoots");
        var parameters = (IDictionary<string, object?>)error.Extensions["parameters"]!;
        parameters["rootCount"].Should().Be(ByInterfaceWideGraphQlTestFixture.WideRoots.ToString());
        parameters["maxRoots"].Should().Be("64");
    }

    // ---- measuring --------------------------------------------------------------------------------------------------

    private readonly record struct Timing(double Median, double P95);

    private async Task RunPageAsync(string interfaceName, string sortPath, int skip, int take, bool count)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query { runtime { byInterface(ckInterfaceId: "Perf/{{interfaceName}}-1", skip: {{skip}}, take: {{take}},
              sortOrder: [{ attributePath: "{{sortPath}}", sortOrder: ASCENDING }]) {
              {{(count ? "totalCount" : "")}} items { __typename ... on Perf{{interfaceName}} { name rank } } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectTokens("data.runtime.byInterface.items[*]").Should().HaveCount(take);
    }

    private async Task RunTypePageAsync(int take)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query { runtime { perfT01(first: {{take}}, sortOrder: [{ attributePath: "name", sortOrder: ASCENDING }]) {
              totalCount items { name rank } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectTokens("data.runtime.perfT01.items[*]").Should().HaveCount(take);
    }

    private static async Task<Timing> TimeAsync(Func<Task> work, int warmUp = WarmUpRuns, int runs = MeasuredRuns)
    {
        for (var i = 0; i < warmUp; i++)
        {
            await work();
        }

        var samples = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await work();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return new Timing(samples[samples.Count / 2], samples[(int)Math.Ceiling(0.95 * samples.Count) - 1]);
    }

    private void Report(string line) => _output.WriteLine($"by-interface perf: {line}");

    // ---- seed data ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Loads <paramref name="entityCount" /> entities evenly over the 30 roots straight into the collections: one
    ///     entity is created through GraphQL and its stored document is the template of all others (so the documents
    ///     have exactly the shape the engine writes), the names are random strings of a fixed seed.
    /// </summary>
    private async Task LoadAsync(int entityCount)
    {
        var create = await _fixture.ExecuteGraphQlAsync("""
            mutation { runtime { perfT01s { create(entities: [{ name: "template", rank: 1 }]) { rtId } } } }
            """);
        create.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(create));

        var roots = ByInterfaceWideGraphQlTestFixture.PerfRoots;
        var template = await _fixture.RawCollection("Perf", 1).Find(new BsonDocument()).FirstAsync();
        var random = new Random(Seed);
        var perRoot = entityCount / roots;
        for (var root = 1; root <= roots; root++)
        {
            var typeName = $"Perf/T{root:D2}";
            var raw = _fixture.RawCollection("Perf", root);
            var count = root == roots ? entityCount - perRoot * (roots - 1) : perRoot;
            if (root == 1)
            {
                count--; // the template stays in
            }

            for (var offset = 0; offset < count; offset += 10_000)
            {
                var batch = new List<BsonDocument>(Math.Min(10_000, count - offset));
                for (var i = offset; i < Math.Min(offset + 10_000, count); i++)
                {
                    var document = template.DeepClone().AsBsonDocument;
                    document["_id"] = ObjectId.GenerateNewId();
                    document["ckTypeId"] = typeName;
                    var attributes = document["attributes"].AsBsonDocument;
                    attributes["name"] = RandomName(random);
                    attributes["rank"] = random.Next(0, 1_000_000);
                    batch.Add(document);
                }

                await raw.InsertManyAsync(batch);
            }
        }
    }

    private static string RandomName(Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        return string.Create(10, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[r.Next(alphabet.Length)];
            }
        });
    }
}
