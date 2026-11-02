using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#8049: a stored query whose field filter, sort order, column, group-by column or attribute-search path reaches a
///     Hidden attribute is refused when it is SAVED (create and update, typed and generic mutations) with
///     <c>ATTRIBUTE_NOT_QUERYABLE</c>, and nothing is stored. Running such a query through GraphQL was already refused,
///     but the export job and the pipeline nodes apply a stored filter as it is, so the row count would be an oracle for
///     the hidden value. Test type: <c>AssetRepositoryIntegrationTest/AccessTestAccount</c> (PasswordHash is Hidden).
///     The guess is a dummy constant; no error may echo it.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class StoredQueryHiddenAttributeTests
{
    private const string AccountType = "AssetRepositoryIntegrationTest/AccessTestAccount";
    private const string GroupType = "AssetRepositoryIntegrationTest/AccessTestGroup";
    private const string Guess = "dummy-guess-8049";

    private readonly GraphQlTestFixture _fixture;

    public StoredQueryHiddenAttributeTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    public static TheoryData<string> HiddenStoredQueries() => new()
    {
        "filter", "filter-like", "sort", "column", "selector", "attribute-search", "group-by", "aggregation-filter"
    };

    private static string Mutation(string shape) => shape switch
    {
        "filter" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("filter")}}", queryCkTypeId: "{{AccountType}}", columns: ["name"], fieldFilter: [{ attributePath: "passwordHash", operator: EQUALS, comparisonValue: "{{Guess}}" }] }]) { rtId } }
            """,
        "filter-like" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("filter-like")}}", queryCkTypeId: "{{AccountType}}", columns: ["name"], fieldFilter: [{ attributePath: "PasswordHash", operator: LIKE, comparisonValue: "{{Guess}}%" }] }]) { rtId } }
            """,
        "sort" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("sort")}}", queryCkTypeId: "{{AccountType}}", columns: ["name"], sorting: [{ attributePath: "passwordHash", sortOrder: ASCENDING }] }]) { rtId } }
            """,
        "column" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("column")}}", queryCkTypeId: "{{AccountType}}", columns: ["name", "passwordHash"] }]) { rtId } }
            """,
        "selector" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("selector")}}", queryCkTypeId: "{{GroupType}}", columns: ["name", "members.{{AccountType}}[passwordHash={{Guess}}]->name"] }]) { rtId } }
            """,
        "attribute-search" => $$"""
            systemSimpleRtQuerys { create(entities: [{ name: "{{Name("attribute-search")}}", queryCkTypeId: "{{AccountType}}", columns: ["name"], attributeSearchFilter: { attributePaths: ["name", "passwordHash"], searchValue: "{{Guess}}" } }]) { rtId } }
            """,
        "group-by" => $$"""
            systemGroupingAggregationRtQuerys { create(entities: [{ name: "{{Name("group-by")}}", queryCkTypeId: "{{AccountType}}", groupingColumns: ["passwordHash"], columns: [{ attributePath: "name", aggregationType: COUNT }] }]) { rtId } }
            """,
        "aggregation-filter" => $$"""
            systemAggregationRtQuerys { create(entities: [{ name: "{{Name("aggregation-filter")}}", queryCkTypeId: "{{AccountType}}", columns: [{ attributePath: "name", aggregationType: COUNT }], fieldFilter: [{ attributePath: "passwordHash", operator: EQUALS, comparisonValue: "{{Guess}}" }] }]) { rtId } }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    [Theory]
    [MemberData(nameof(HiddenStoredQueries))]
    public async Task TypedCreate_OfAQueryReachingAHiddenAttribute_IsRefused_AndNothingIsStored(string shape)
    {
        var result = await _fixture.ExecuteGraphQlAsync($"mutation {{ runtime {{ {Mutation(shape)} }} }}");

        AssertNotQueryable(result);
        (await CountQueriesNamedAsync(Name(shape))).Should().Be(0, "a refused query is not stored");
    }

    [Fact]
    public async Task GenericCreate_OfAQueryWithAHiddenFilter_IsRefused()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityInput!]!) {
              runtime { runtimeEntities { create(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = "System/SimpleRtQuery",
                    attributes = new object[]
                    {
                        new { attributeName = "name", value = (object)Name("generic") },
                        new { attributeName = "queryCkTypeId", value = (object)AccountType },
                        new { attributeName = "columns", value = (object)new[] { "name" } },
                        new
                        {
                            attributeName = "fieldFilter",
                            value = (object)new[]
                            {
                                new { attributePath = "passwordHash", @operator = "EQUALS", comparisonValue = Guess }
                            }
                        }
                    }
                }
            }
        }));

        AssertNotQueryable(result);
        (await CountQueriesNamedAsync(Name("generic"))).Should().Be(0);
    }

    [Fact]
    public async Task Update_ThatAddsAHiddenFilter_IsRefused_AndTheStoredQueryIsUnchanged()
    {
        var rtId = await CreateVisibleQueryAsync(Name("update-filter"), AccountType, "name");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { update(entities: [{ rtId: "{{rtId}}", item: {
              fieldFilter: [{ attributePath: "passwordHash", operator: EQUALS, comparisonValue: "{{Guess}}" }] } }]) { rtId } } } }
            """);

        AssertNotQueryable(result);
        var stored = await ExecuteOkAsync($$"""
            query { runtime { systemSimpleRtQuery(rtIds: ["{{rtId}}"]) { items { fieldFilter { attributePath } } } } }
            """);
        stored.SelectTokens("$..attributePath").Select(t => t.Value<string>()).Should().Equal("name");
    }

    [Fact]
    public async Task Update_ThatOnlyRetargetsTheQueryType_IsJudgedWithTheStoredFilter()
    {
        // A filter on "passwordHash" means nothing for the group type (no such attribute); moving the query onto the
        // account type would turn the stored filter into an oracle.
        var rtId = await CreateVisibleQueryAsync(Name("retarget"), GroupType, "passwordHash");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { update(entities: [{ rtId: "{{rtId}}", item: {
              queryCkTypeId: "{{AccountType}}" } }]) { rtId } } } }
            """);

        AssertNotQueryable(result);
    }

    [Fact]
    public async Task QueriesOnVisibleAttributes_AreStoredAndUpdated_AsBefore()
    {
        var rtId = await CreateVisibleQueryAsync(Name("visible"), AccountType, "name");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { update(entities: [{ rtId: "{{rtId}}", item: {
              description: "renamed", sorting: [{ attributePath: "label", sortOrder: DESCENDING }] } }]) { rtId } } } }
            """);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
    }

    // AB#8052: a selector value that contains ']' used to keep the part after the first ']' in the error message, so a part
    // of the guess was echoed. (A value with '[' is no valid path for the engine, RtPathEvaluator.TokenizePath refuses it.)
    [Theory]
    [InlineData("close", "x]" + Guess)]
    [InlineData("double-close", "x]]" + Guess)]
    public async Task SelectorValueWithBrackets_IsRefused_WithoutEchoingAnyPartOfTheValue(string shape, string value)
    {
        var name = Name("selector-brackets-" + shape);
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { create(entities: [{ name: "{{name}}", queryCkTypeId: "{{GroupType}}",
              columns: ["name", "members.{{AccountType}}[passwordHash='{{value}}']->name"] }]) { rtId } } } }
            """);

        AssertNotQueryable(result);
        var json = _fixture.SerializeGraphQl(result);
        json.Should().NotContain("dummy-guess");
        result.Errors!.Select(e => e.Message).Should().Contain(m => m.Contains("[passwordHash=…]->name"));
        (await CountQueriesNamedAsync(name)).Should().Be(0);
    }

    // AB#8081: a quoted value still open at the last ']' — the text after it looks like a path suffix ("->dummyguess0001")
    // but belongs to the value; the redaction runs to the end of the path.
    [Fact]
    public async Task SelectorValueWithOpenQuoteAtLastBracket_IsRefused_WithoutEchoingAnyPartOfTheValue()
    {
        const string pathLikeGuess = "dummyguess0001";
        var name = Name("selector-open-quote");
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { create(entities: [{ name: "{{name}}", queryCkTypeId: "{{GroupType}}",
              columns: ["name", "members.{{AccountType}}[passwordHash='x]->{{pathLikeGuess}}"] }]) { rtId } } } }
            """);

        result.Errors.Should().NotBeNullOrEmpty();
        var json = _fixture.SerializeGraphQl(result);
        json.Should().NotContain(pathLikeGuess);
        (await CountQueriesNamedAsync(name)).Should().Be(0);
    }

    private static string Name(string shape) => $"ab8049-{shape}";

    private async Task<string> CreateVisibleQueryAsync(string name, string queryCkTypeId, string filterPath)
    {
        var answer = await ExecuteOkAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { create(entities: [{ name: "{{name}}", queryCkTypeId: "{{queryCkTypeId}}",
              columns: ["name"], fieldFilter: [{ attributePath: "{{filterPath}}", operator: EQUALS, comparisonValue: "x" }] }]) { rtId } } } }
            """);
        return answer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private async Task<int> CountQueriesNamedAsync(string name)
    {
        var answer = await ExecuteOkAsync($$"""
            query { runtime { runtimeEntities(ckId: "System/PersistentQuery",
              fieldFilter: [{ attributePath: "name", operator: EQUALS, comparisonValue: "{{name}}" }]) { totalCount } } }
            """);
        return answer.SelectToken("data.runtime.runtimeEntities.totalCount")!.Value<int>();
    }

    private void AssertNotQueryable(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        result.Errors!.Select(e => e.Code).Should().Contain("ATTRIBUTE_NOT_QUERYABLE", json);
        json.Should().NotContain(Guess, "an access error never echoes a value");
    }

    private async Task<JObject> ExecuteOkAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }
}
