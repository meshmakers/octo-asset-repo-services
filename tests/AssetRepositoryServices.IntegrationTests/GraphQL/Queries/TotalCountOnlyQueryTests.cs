using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     AB#8895: a runtime-entity connection that selects only <c>totalCount</c> is answered by the server-side count
///     (no document loaded) and returns exactly the count of the same query that also selects the items - for the
///     typed and the generic runtime-entity query and the rows of a persistent query, with filters, search, alias,
///     <c>__typename</c> and paging arguments.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public class TotalCountOnlyQueryTests
{
    private const string Customer = "assetRepositoryIntegrationTestCustomer";
    private const string CustomerCkId = "AssetRepositoryIntegrationTest/Customer";

    private readonly GraphQlTestFixture _fixture;

    public TotalCountOnlyQueryTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    public static TheoryData<string, string> TypedQueryArguments() => new()
    {
        { "NoFilter", "" },
        { "FieldFilterLike", "(fieldFilter: [{attributePath: \"lastName\", comparisonValue: \"er\", operator: LIKE}])" },
        { "FieldFilterEquals", "(fieldFilter: [{attributePath: \"lastName\", comparisonValue: \"Müller\", operator: EQUALS}])" },
        { "NoMatch", "(fieldFilter: [{attributePath: \"lastName\", comparisonValue: \"no-such-8895\", operator: EQUALS}])" },
        { "AttributeSearch", "(searchFilter: {type: ATTRIBUTE_FILTER, attributePaths: [\"lastName\"], searchTerm: \"M\"})" },
        { "Sorted", "(sortOrder: [{attributePath: \"lastName\", sortOrder: DESCENDING}])" },
        { "Paged", "(first: 1, after: \"" + Cursor(0) + "\")" }
    };

    [Theory]
    [MemberData(nameof(TypedQueryArguments))]
    public async Task TypedQuery_TotalCountOnly_EqualsTheCountWithItems(string shape, string arguments)
    {
        var countOnly = await CountAsync($"query {{ runtime {{ {Customer}{arguments} {{ totalCount }} }} }}",
            $"data.runtime.{Customer}.totalCount");
        var withItems = await CountAsync($"query {{ runtime {{ {Customer}{arguments} {{ totalCount items {{ rtId }} }} }} }}",
            $"data.runtime.{Customer}.totalCount");

        countOnly.Should().Be(withItems, shape);
        if (shape == "NoMatch")
        {
            countOnly.Should().Be(0);
        }
        else
        {
            countOnly.Should().BeGreaterThan(0, shape);
        }
    }

    [Fact]
    public async Task TypedQuery_UnpagedItems_CountEqualsTheNumberOfItems()
    {
        var result = await RunAsync($"query {{ runtime {{ {Customer} {{ totalCount items {{ rtId }} }} }} }}");
        var countOnly = await CountAsync($"query {{ runtime {{ {Customer} {{ totalCount }} }} }}",
            $"data.runtime.{Customer}.totalCount");

        ((JArray)result.SelectToken($"data.runtime.{Customer}.items")!).Count.Should().Be(countOnly);
    }

    [Fact]
    public async Task GenericQuery_TotalCountOnly_EqualsTheCountWithItems()
    {
        var arguments = $"ckId: \"{CustomerCkId}\"";
        var countOnly = await CountAsync($"query {{ runtime {{ runtimeEntities({arguments}) {{ totalCount }} }} }}",
            "data.runtime.runtimeEntities.totalCount");
        var withItems = await CountAsync(
            $"query {{ runtime {{ runtimeEntities({arguments}) {{ totalCount items {{ rtId }} }} }} }}",
            "data.runtime.runtimeEntities.totalCount");

        countOnly.Should().Be(withItems).And.BeGreaterThan(0);
    }

    [Fact]
    public async Task AliasAndTypename_StillAnswerTheCount()
    {
        var expected = await CountAsync($"query {{ runtime {{ {Customer} {{ totalCount items {{ rtId }} }} }} }}",
            $"data.runtime.{Customer}.totalCount");

        var answer = await RunAsync($"query {{ runtime {{ c: {Customer} {{ __typename howMany: totalCount }} }} }}");

        answer.SelectToken("data.runtime.c.howMany")!.Value<int>().Should().Be(expected);
        answer.SelectToken("data.runtime.c.__typename")!.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PersistentQueryRows_TotalCountOnly_EqualsTheCountWithRows()
    {
        var queryRtId = await CreateSimpleQueryAsync();
        try
        {
            var countOnly = await CountAsync(RowsQuery(queryRtId, "totalCount"),
                "data.runtime.runtimeQuery.items[0].rows.totalCount");
            var withRows = await CountAsync(RowsQuery(queryRtId, "totalCount items { __typename }"),
                "data.runtime.runtimeQuery.items[0].rows.totalCount");
            var filteredCountOnly = await CountAsync(RowsQuery(queryRtId, "totalCount",
                    "(fieldFilter: [{attributePath: \"operatingStatus\", comparisonValue: \"no-such-8895\", operator: NOT_EQUALS}])"),
                "data.runtime.runtimeQuery.items[0].rows.totalCount");
            var filteredWithRows = await CountAsync(RowsQuery(queryRtId, "totalCount items { __typename }",
                    "(fieldFilter: [{attributePath: \"operatingStatus\", comparisonValue: \"no-such-8895\", operator: NOT_EQUALS}])"),
                "data.runtime.runtimeQuery.items[0].rows.totalCount");

            countOnly.Should().Be(withRows).And.BeGreaterThan(0);
            filteredCountOnly.Should().Be(filteredWithRows);
        }
        finally
        {
            await DeleteSimpleQueryAsync(queryRtId);
        }
    }

    private static string RowsQuery(string queryRtId, string selection, string rowArguments = "") => $$"""
        query { runtime { runtimeQuery(rtId: "{{queryRtId}}") { items { rows{{rowArguments}} { {{selection}} } } } } }
        """;

    private static string Cursor(int offset) =>
        Convert.ToBase64String(global::System.Text.Encoding.UTF8.GetBytes($"arrayconnection:{offset}"));

    private async Task<JObject> RunAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var answer = JObject.Parse(_fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(answer.ToString());
        return answer;
    }

    private async Task<int> CountAsync(string query, string path)
    {
        var answer = await RunAsync(query);
        var token = answer.SelectToken(path);
        token.Should().NotBeNull(answer.ToString());
        return token!.Value<int>();
    }

    private async Task<string> CreateSimpleQueryAsync()
    {
        var answer = await RunAsync("""
            mutation { runtime { systemSimpleRtQuerys { create(entities: [{
                name: "TotalCountOnly8895"
                queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                columns: ["rtWellKnownName", "meterReading", "operatingStatus"]
            }]) { rtId } } } }
            """);
        return answer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private async Task DeleteSimpleQueryAsync(string rtId)
    {
        await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { delete(rtIds: ["{{rtId}}"]) { deletedCount } } } }
            """);
    }
}
