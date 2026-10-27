using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.ByInterface;

/// <summary>
///     AB#6932: <c>runtime.byInterface</c>, the GraphQL layer over the engine's interface query (AB#7019). Seed: nine
///     entities of A, B and BChild (<see cref="ByInterfaceGraphQlTestFixture.Seed" />), one entity of a type that does
///     not implement the interface. Type A is protected by a data policy.
/// </summary>
[Collection(ByInterfaceCollection.Name)]
public class ByInterfaceQueryTests
{
    private const string RankedId = "AssetRepositoryIntegrationTest/Ranked-1";
    private const string Prefix = "AssetRepositoryIntegrationTest";
    private const string Items = """
        items { __typename ... on AssetRepositoryIntegrationTestRanked { name rank } }
        """;

    private readonly ByInterfaceGraphQlTestFixture _fixture;

    public ByInterfaceQueryTests(ByInterfaceGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static string Field(string arguments, string selection) =>
        $"query {{ runtime {{ byInterface({arguments}) {{ {selection} }} }} }}";

    private async Task<(JObject Json, ExecutionResult Result)> RunAsync(string arguments, string selection,
        ClaimsPrincipal? user = null)
    {
        var result = await _fixture.ExecuteGraphQlAsync(Field(arguments, selection),
            user: user ?? ByInterfaceGraphQlTestFixture.PrivilegedUser);
        return (JObject.Parse(_fixture.SerializeGraphQl(result)), result);
    }

    private static List<string> Names(JObject json) =>
        json.SelectTokens("data.runtime.byInterface.items[*].name").Select(t => t.Value<string>()!).ToList();

    private static List<string> TypeNames(JObject json) =>
        json.SelectTokens("data.runtime.byInterface.items[*].__typename").Select(t => t.Value<string>()!).ToList();

    private static string Rank(string order) => $"sortOrder: [{{ attributePath: \"rank\", sortOrder: {order} }}]";

    [Fact]
    public async Task AllImplementers_InGlobalOrder_WithExactTypenamesAndInterfaceFragment()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{RankedId}\", take: 100, {Rank("ASCENDING")}", Items);

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        Names(json).Should().Equal(ByInterfaceGraphQlTestFixture.Seed.Select(s => s.Name));
        TypeNames(json).Should().Equal(ByInterfaceGraphQlTestFixture.Seed.Select(s => Prefix + s.Type),
            "__typename is the exact type of each entity (the child of B is not reported as B), the non-implementer is absent");
    }

    [Fact]
    public async Task TypeSpecificFields_AreReachableThroughInlineFragments()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{RankedId}\", take: 100, {Rank("ASCENDING")}",
            $"items {{ ... on {Prefix}InterfaceQueryBChild {{ name extraChild }} }}");

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        var children = json.SelectTokens("data.runtime.byInterface.items[?(@.extraChild)]").ToList();
        children.Select(c => c["name"]!.Value<string>()).Should().Equal("c-1", "c-2");
        children.Select(c => c["extraChild"]!.Value<string>()).Should().Equal("extra-c-1", "extra-c-2");
    }

    [Fact]
    public async Task Descending_SkipAndTake_PageTheGlobalOrder()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{RankedId}\", skip: 2, take: 3, {Rank("DESCENDING")}", Items);

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        // Descending ranks: a-4 70, b-3 60, a-3 50, c-2 45, b-2 40, ...
        Names(json).Should().Equal("a-3", "c-2", "b-2");
    }

    [Fact]
    public async Task Filter_OnAnInterfaceMember_AppliesToEveryImplementer()
    {
        var (json, result) = await RunAsync(
            $"ckInterfaceId: \"{RankedId}\", take: 100, {Rank("ASCENDING")}, " +
            "fieldFilter: [{ attributePath: \"rank\", operator: GREATER_EQUAL_THAN, comparisonValue: 40 }]", Items);

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        Names(json).Should().Equal("b-2", "c-2", "a-3", "b-3", "a-4");
    }

    [Fact]
    public async Task SortOnAStringMember_IsTotalAcrossCollections()
    {
        var (json, result) = await RunAsync(
            $"ckInterfaceId: \"{RankedId}\", take: 100, sortOrder: [{{ attributePath: \"name\", sortOrder: ASCENDING }}]", Items);

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        Names(json).Should().Equal("a-1", "a-2", "a-3", "a-4", "b-1", "b-2", "b-3", "c-1", "c-2");
    }

    [Fact]
    public async Task TotalCount_IsReturnedWhenSelected_AndIgnoresSkipAndTake()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{RankedId}\", skip: 1, take: 2, {Rank("ASCENDING")}",
            "totalCount items { __typename }");

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        json.SelectToken("data.runtime.byInterface.totalCount")!.Value<int>().Should().Be(9);
        json.SelectTokens("data.runtime.byInterface.items[*]").Should().HaveCount(2);
    }

    [Fact]
    public async Task InterfaceWithoutImplementers_ReturnsAnEmptyConnectionWithCountZero()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{Prefix}/Labeled-2\", take: 10", "totalCount items { __typename }");

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        json.SelectToken("data.runtime.byInterface.totalCount")!.Value<int>().Should().Be(0);
        json.SelectToken("data.runtime.byInterface.items")!.Should().BeEmpty();
    }

    [Fact]
    public async Task TakeIsRequired_SkipDefaultsToZero_Schema()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            { __type(name: "RuntimeModelQuery") { fields { name args { name defaultValue type { kind ofType { name } } } } } }
            """);
        var json = JObject.Parse(_fixture.SerializeGraphQl(result));
        var args = json.SelectToken("data.__type.fields[?(@.name=='byInterface')].args")!.ToDictionary(a => a["name"]!.Value<string>()!);

        args["take"]["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        args["ckInterfaceId"]["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        args["skip"]["type"]!["kind"]!.Value<string>().Should().Be("SCALAR");
        args["skip"]["defaultValue"]!.Value<string>().Should().Be("0");

        var missingTake = await _fixture.ExecuteGraphQlAsync(Field($"ckInterfaceId: \"{RankedId}\"", "totalCount"));
        missingTake.Errors.Should().NotBeNullOrEmpty("a query without take fails validation");
    }

    [Fact]
    public async Task Schema_GainsExactlyTheFieldAndTwoTypes_TheUnionListsEveryConcreteImplementer()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            { __schema { types { name possibleTypes { name } } } runtime: __type(name: "RuntimeModelQuery") { fields { name } } }
            """);
        var json = JObject.Parse(_fixture.SerializeGraphQl(result));

        json.SelectTokens("data.__schema.types[?(@.name =~ /ByInterface/)].name").Select(t => t.Value<string>())
            .Should().BeEquivalentTo("RtEntityByInterface", "RtEntityByInterfaceConnection");
        json.SelectTokens("data.runtime.fields[*].name").Select(t => t.Value<string>()).Should().Contain("byInterface");
        json.SelectTokens("data.__schema.types[?(@.name=='RtEntityByInterface')].possibleTypes[*].name")
            .Select(t => t.Value<string>()).Should()
            .Contain([
                Prefix + "InterfaceQueryA", Prefix + "InterfaceQueryB", Prefix + "InterfaceQueryBChild",
                Prefix + "AccessTestAccount", Prefix + "AccessTestGroup"])
            .And.NotContain(Prefix + "InterfaceQueryOther", "only types that implement a CK interface are members");
    }

    [Fact]
    public async Task QueryInATransactionSession_ReturnsTheSameResultAsWithout()
    {
        var arguments = $"ckInterfaceId: \"{RankedId}\", skip: 1, take: 6, {Rank("ASCENDING")}";
        var (plain, plainResult) = await RunAsync(arguments, "totalCount " + Items);

        _fixture.RunQueriesInTransaction = true;
        (JObject Json, ExecutionResult Result) inTransactionRun;
        try
        {
            inTransactionRun = await RunAsync(arguments, "totalCount " + Items);
        }
        finally
        {
            _fixture.RunQueriesInTransaction = false;
        }

        var (inTransaction, transactionResult) = inTransactionRun;
        plainResult.Errors.Should().BeNullOrEmpty(plain.ToString());
        transactionResult.Errors.Should().BeNullOrEmpty(inTransaction.ToString());
        inTransaction["data"]!.ToString().Should().Be(plain["data"]!.ToString());
        Names(inTransaction).Should().Equal("b-1", "c-1", "a-2", "b-2", "c-2", "a-3");
    }

    // ---- Permissions: same behaviour as the per-type queries ----------------------------------------------------

    [Fact]
    public async Task RestrictedUser_NeverSeesTheDeniedType_AndTheCountDoesNotLeakDeniedRows()
    {
        var (json, result) = await RunAsync($"ckInterfaceId: \"{RankedId}\", take: 100, {Rank("ASCENDING")}",
            "totalCount " + Items, ByInterfaceGraphQlTestFixture.RestrictedUser);

        result.Errors.Should().BeNullOrEmpty(json.ToString());
        Names(json).Should().Equal("b-1", "c-1", "b-2", "c-2", "b-3");
        TypeNames(json).Should().NotContain(Prefix + "InterfaceQueryA");
        json.SelectToken("data.runtime.byInterface.totalCount")!.Value<int>().Should().Be(5);
    }

    [Fact]
    public async Task RestrictedUser_SeesTheSameRowsAsThePerTypeQueriesOfTheAllowedTypes()
    {
        var perType = await _fixture.ExecuteGraphQlAsync("""
            query { runtime { assetRepositoryIntegrationTestInterfaceQueryA(first: 50) { totalCount }
                              assetRepositoryIntegrationTestInterfaceQueryB(first: 50) { totalCount } } }
            """, user: ByInterfaceGraphQlTestFixture.RestrictedUser);
        var perTypeJson = JObject.Parse(_fixture.SerializeGraphQl(perType));

        perTypeJson.SelectToken("data.runtime.assetRepositoryIntegrationTestInterfaceQueryA.totalCount")!.Value<int>()
            .Should().Be(0, "the per-type query hides the protected type for the restricted user as well");
        perTypeJson.SelectToken("data.runtime.assetRepositoryIntegrationTestInterfaceQueryB.totalCount")!.Value<int>()
            .Should().Be(5, "B and its child share the collection; the per-type query of B includes derived types");
    }

    [Fact]
    public async Task PrivilegedUser_SeesTheProtectedType()
    {
        var (json, _) = await RunAsync($"ckInterfaceId: \"{RankedId}\", take: 100", "totalCount " + Items);

        json.SelectToken("data.runtime.byInterface.totalCount")!.Value<int>().Should().Be(9);
        TypeNames(json).Should().Contain(Prefix + "InterfaceQueryA");
    }

    // ---- Fail clearly: one test per refusal reason of AB#7019 ---------------------------------------------------

    [Theory]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Nope-1\", take: 5", "UnknownInterface", "ckInterfaceId")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: 5, searchFilter: { searchTerm: \"a\" }",
        "TextSearchNotSupported", "ckInterfaceId")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: 5, aggregations: { countAttributePaths: [\"name\"] }",
        "AggregationNotSupported", "ckInterfaceId")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: 5, sortOrder: [{ attributePath: \"memberOf->name\", sortOrder: ASCENDING }]",
        "NavigationNotSupported", "attributePath")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", skip: 900, take: 200",
        "PageTooLarge", "maxSkipPlusTake")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: 5, fieldFilter: [{ attributePath: \"extraChild\", operator: EQUALS, comparisonValue: \"x\" }]",
        "UnknownMember", "attributePath")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: 5, sortOrder: [{ attributePath: \"extraChild\", sortOrder: ASCENDING }]",
        "UnknownMember", "usage")]
    public async Task Refusal_ReachesTheClientWithNumberReasonAndParameters_NotAsAServerError(string arguments,
        string reason, string parameter)
    {
        var (json, result) = await RunAsync(arguments, "totalCount items { __typename }");

        result.Errors.Should().ContainSingle(json.ToString());
        var error = result.Errors![0];
        error.Code.Should().Be("INTERFACE_QUERY_REFUSED");
        error.Extensions.Should().NotBeNull();
        error.Extensions!["messageNumber"].Should().Be(7019);
        error.Extensions["reason"].Should().Be(reason);
        error.Extensions["parameters"].Should().BeAssignableTo<IDictionary<string, object?>>()
            .Which.Should().ContainKey(parameter).And.ContainKey("reason");
        json.SelectToken("data.runtime.byInterface")!.Type.Should().Be(JTokenType.Null);
        error.Message.Should().NotContain("An error occurred");
    }

    [Theory]
    [InlineData("fieldFilter: [{ attributePath: \"token\", operator: EQUALS, comparisonValue: \"x\" }]")]
    [InlineData("sortOrder: [{ attributePath: \"token\", sortOrder: ASCENDING }]")]
    public async Task SecretMemberOfTheInterface_CannotBeUsedAsFilterOrSortOracle(string argument)
    {
        // The attribute guards are bound to one type: they run against every implementing type, as on the type query.
        var (json, result) = await RunAsync($"ckInterfaceId: \"{Prefix}/Guarded-1\", take: 5, {argument}", "totalCount");

        result.Errors.Should().ContainSingle(json.ToString());
        result.Errors![0].Code.Should().Be("SecretAttributeNotQueryable");
    }

    [Fact]
    public async Task GeospatialFilter_IsRefusedAsNavigation()
    {
        var (json, result) = await RunAsync(
            $"ckInterfaceId: \"{RankedId}\", take: 5, geoNearFilter: {{ attributeName: \"name\", point: {{ coordinates: {{ latitude: 1.0, longitude: 1.0 }} }}, minDistance: 0, maxDistance: 10 }}",
            "totalCount");

        result.Errors.Should().ContainSingle(json.ToString());
        result.Errors![0].Extensions!["reason"].Should().Be("NavigationNotSupported");
    }

    [Theory]
    [InlineData("ckInterfaceId: \"not-a-ck-id\", take: 5", "ckInterfaceId")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", take: -1", "take")]
    [InlineData("ckInterfaceId: \"AssetRepositoryIntegrationTest/Ranked-1\", skip: -3, take: 1", "skip")]
    public async Task MalformedArguments_AreClientErrorsWithAStableCode(string arguments, string argument)
    {
        var (json, result) = await RunAsync(arguments, "totalCount");

        result.Errors.Should().ContainSingle(json.ToString());
        result.Errors![0].Code.Should().Be("INVALID_ARGUMENT");
        result.Errors[0].Extensions!["argument"].Should().Be(argument);
    }
}
