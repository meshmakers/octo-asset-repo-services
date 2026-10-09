using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     CK v2 Phase 0 review H1: a Hidden attribute must not be usable as filter / sort / search / aggregation oracle
///     through any association or navigation connection. Each entry point is tried with the hidden path (rejected
///     with <c>ATTRIBUTE_NOT_QUERYABLE</c>) and with a visible path (positive control, finds the member).
///     Model: <c>AccessTestAccount</c> --AccessTestMembership--> <c>AccessTestGroup</c> (navigation <c>members</c> on
///     the group).
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class HiddenAttributeNavigationTests
{
    private const string AccountCkTypeId = "AssetRepositoryIntegrationTest/AccessTestAccount";
    private const string GroupCkTypeId = "AssetRepositoryIntegrationTest/AccessTestGroup";
    private const string RoleId = "AssetRepositoryIntegrationTest/AccessTestMembership";
    private const string StoredHash = "AQAAAAIAAYagAAAAENavigationHashThatMustNeverLeave";

    private const string HiddenFilter =
        """fieldFilter: [{ attributePath: "passwordHash", operator: LIKE, comparisonValue: "AQ" }]""";

    private const string VisibleFilter =
        """fieldFilter: [{ attributePath: "name", operator: EQUALS, comparisonValue: "nav-member" }]""";

    private readonly GraphQlTestFixture _fixture;

    public HiddenAttributeNavigationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    public static TheoryData<string> HiddenArguments => new()
    {
        HiddenFilter,
        """fieldFilter: [{ attributePath: "passwordHash", operator: IS_NOT_NULL }]""",
        """fieldFilter: [{ attributePath: "PasswordHash", operator: EQUALS, comparisonValue: "x" }]""",
        """sortOrder: [{ attributePath: "passwordHash", sortOrder: ASCENDING }]""",
        """searchFilter: { type: ATTRIBUTE_FILTER, attributePaths: ["passwordHash"], searchTerm: "AQ" }""",
        """aggregations: { maxValueAttributePaths: ["passwordHash"] }"""
    };

    [Theory]
    [MemberData(nameof(HiddenArguments))]
    public async Task TypedNavigation_HiddenPath_IsRejected(string arguments)
    {
        var groupRtId = await CreateGroupWithMemberAsync();

        await AssertRejectedAsync(TypedNavigationQuery(groupRtId, arguments));
    }

    [Theory]
    [MemberData(nameof(HiddenArguments))]
    public async Task TypedGenericAssociations_HiddenPath_IsRejected(string arguments)
    {
        var groupRtId = await CreateGroupWithMemberAsync();

        await AssertRejectedAsync(TypedGenericQuery(groupRtId, AccountCkTypeId, arguments, indirect: false));
        await AssertRejectedAsync(TypedGenericQuery(groupRtId, AccountCkTypeId, arguments, indirect: true));
    }

    [Theory]
    [MemberData(nameof(HiddenArguments))]
    public async Task GenericTargets_HiddenPath_IsRejected(string arguments)
    {
        var groupRtId = await CreateGroupWithMemberAsync();

        await AssertRejectedAsync(GenericTargetsQuery(groupRtId, AccountCkTypeId, arguments, indirect: false));
        await AssertRejectedAsync(GenericTargetsQuery(groupRtId, AccountCkTypeId, arguments, indirect: true));
    }

    [Fact]
    public async Task PolymorphicTarget_HiddenOnDerivedType_IsRejected()
    {
        // Target System/Entity: the hidden attribute only exists on the derived AccessTestAccount.
        var groupRtId = await CreateGroupWithMemberAsync();

        await AssertRejectedAsync(TypedGenericQuery(groupRtId, "System/Entity", HiddenFilter, indirect: false));
        await AssertRejectedAsync(GenericTargetsQuery(groupRtId, "System/Entity", HiddenFilter, indirect: false));
    }

    [Fact]
    public async Task VisiblePaths_StillWork_OnEveryEntryPoint()
    {
        var groupRtId = await CreateGroupWithMemberAsync();

        (await TotalCountAsync(TypedNavigationQuery(groupRtId, VisibleFilter))).Should().Be(1);
        (await TotalCountAsync(TypedGenericQuery(groupRtId, AccountCkTypeId, VisibleFilter, false))).Should().Be(1);
        (await TotalCountAsync(GenericTargetsQuery(groupRtId, AccountCkTypeId, VisibleFilter, false))).Should().Be(1);
    }

    // Re-review N1: the collector form of a selector column (lower-camel type name, quoted value). The selector
    // is stripped for matching, so these paths resolve to the visible column "...->name".
    private const string VisibleSelectorColumn =
        "members.assetRepositoryIntegrationTestAccessTestAccount[name='nav-member']->name";

    private const string HiddenSelectorColumn =
        "members.assetRepositoryIntegrationTestAccessTestAccount[passwordHash='" + StoredHash + "']->name";

    [Fact]
    public async Task SelectorColumn_VisibleKey_ResolvesTheMember()
    {
        // Positive control: proves the path form is a real, resolvable column.
        await CreateGroupWithMemberAsync();

        var result = await _fixture.ExecuteGraphQlAsync(TransientSelectorQuery(VisibleSelectorColumn));

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectTokens("$..cells.items[*].value").Select(v => v.ToString())
            .Should().Contain("nav-member");
    }

    [Fact]
    public async Task SelectorColumn_HiddenKey_IsRejected_Transient()
    {
        // Without the guard the cell is filled exactly when the stored hash equals the guess (equality oracle).
        await CreateGroupWithMemberAsync();

        await AssertRejectedAsync(TransientSelectorQuery(HiddenSelectorColumn));
    }

    [Fact]
    public async Task SelectorColumn_HiddenKey_IsRejected_StoredQuery()
    {
        await CreateGroupWithMemberAsync();
        var queryRtId = await CreateAsync(new
        {
            ckTypeId = "System/SimpleRtQuery",
            attributes = new object[]
            {
                new { attributeName = "name", value = "hidden-selector" },
                new { attributeName = "queryCkTypeId", value = GroupCkTypeId },
                new { attributeName = "columns", value = new[] { "name", HiddenSelectorColumn } }
            }
        });

        await AssertRejectedAsync($$"""
            query { runtime { runtimeQuery(rtId: "{{queryRtId}}") {
              items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } } } } }
            """);
    }

    private static string TransientSelectorQuery(string column) => $$"""
        query { runtime { transientQuery {
          simple(ckId: "{{GroupCkTypeId}}", columnPaths: ["name", "{{column}}"]) {
            items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } } } } } }
        """;

    private static string TypedNavigationQuery(string groupRtId, string arguments) => $$"""
        query { runtime { assetRepositoryIntegrationTestAccessTestGroup(rtIds: ["{{groupRtId}}"]) {
          items { members(ckTypeIds: ["{{AccountCkTypeId}}"], {{arguments}}) { totalCount } } } } }
        """;

    private static string TypedGenericQuery(string groupRtId, string ckId, string arguments, bool indirect) => $$"""
        query { runtime { assetRepositoryIntegrationTestAccessTestGroup(rtIds: ["{{groupRtId}}"]) {
          items { associations(roleId: "{{RoleId}}", direction: INBOUND, ckId: "{{ckId}}",
                               includeIndirect: {{(indirect ? "true" : "false")}}, {{arguments}}) { totalCount } } } } }
        """;

    private static string GenericTargetsQuery(string groupRtId, string ckId, string arguments, bool indirect) => $$"""
        query { runtime { runtimeEntities(ckId: "{{GroupCkTypeId}}", rtIds: ["{{groupRtId}}"]) {
          items { associations { targets(roleId: "{{RoleId}}", direction: INBOUND, ckId: "{{ckId}}",
                                         includeIndirect: {{(indirect ? "true" : "false")}}, {{arguments}}) { totalCount } } } } } }
        """;

    private async Task AssertRejectedAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);

        result.Errors.Should().NotBeNull(json);
        result.Errors!.Select(e => e.Code).Should().Contain("ATTRIBUTE_NOT_QUERYABLE", json);
        result.Data.Should().NotBeNull();
        json.Should().NotContain("nav-member", "no cell may be filled");
        // F1.5-S4 (AB#5923): access errors carry no value - not even the caller's guess in a selector.
        json.Should().NotContain(StoredHash);
    }

    private async Task<int> TotalCountAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectTokens("$..totalCount").Last().Value<int>();
    }

    private async Task<string> CreateGroupWithMemberAsync()
    {
        var groupRtId = await CreateAsync(new
        {
            ckTypeId = GroupCkTypeId,
            attributes = new object[] { new { attributeName = "name", value = "nav-group" } }
        });

        var accountRtId = await CreateAsync(new
        {
            ckTypeId = AccountCkTypeId,
            attributes = new object[]
            {
                new { attributeName = "name", value = "nav-member" },
                new
                {
                    attributeName = "memberOf",
                    value = new object[] { new { target = new { rtId = groupRtId, ckTypeId = GroupCkTypeId } } }
                }
            }
        });

        await _fixture.SetRawAttributeValueInMongoDb(accountRtId, "passwordHash", new BsonString(StoredHash),
            "AssetRepositoryIntegrationTestAccessTestAccount");
        return groupRtId;
    }

    private async Task<string> CreateAsync(object entity)
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityInput!]!) {
              runtime { runtimeEntities { create(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new { entities = new[] { entity } }));
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
    }
}
