using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     A nested association connection that selects <c>totalCount</c> alone is answered by the repository's
///     count-only form and returns, per parent, exactly the total of the same connection with items - with an omitted
///     <c>first</c>, <c>first: 0</c> and a larger <c>first</c>, for the typed navigation field, the generic
///     <c>associations(…)</c> of a typed entity and <c>associations { targets }</c> of a generic entity. The sample data
///     has two readings below car 1.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public class NestedAssociationTotalCountOnlyTests
{
    private const string ReadingCkTypeId = "AssetRepositoryIntegrationTest/VehicleReading";
    private const string CarCkTypeId = "AssetRepositoryIntegrationTest/Car";
    private const string Car1 = "67000004dddd4444eeee0001";

    private readonly GraphQlTestFixture _fixture;

    public NestedAssociationTotalCountOnlyTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    public static TheoryData<string, string> Forms() => new()
    {
        { "TypedNavigation", "" },
        { "TypedNavigation", "first: 0" },
        { "TypedNavigation", "first: 5" },
        { "GenericOfTypedEntity", "" },
        { "GenericOfTypedEntity", "first: 0" },
        { "GenericOfTypedEntity", "first: 5" },
        { "GenericTargets", "" },
        { "GenericTargets", "first: 0" },
        { "GenericTargets", "first: 5" }
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task TotalCountOnly_EqualsTheTotalWithItems_PerParent(string form, string paging)
    {
        var countOnly = await TotalsPerParentAsync(form, paging, "totalCount");
        var withItems = await TotalsPerParentAsync(form, "first: 50", "totalCount items { __typename }");

        countOnly.Should().Equal(withItems, $"{form}({paging})");
        countOnly[Car1].Should().Be(2, "car 1 has two readings");
    }

    [Fact]
    public async Task TotalCountOnlyWithTypename_AnswersTheCount()
    {
        var totals = await TotalsPerParentAsync("TypedNavigation", "", "__typename totalCount");

        totals[Car1].Should().Be(2);
    }

    private async Task<Dictionary<string, int>> TotalsPerParentAsync(string form, string paging, string selection)
    {
        var withComma = paging.Length == 0 ? "" : ", " + paging;
        var (query, itemsPath, connectionPath) = form switch
        {
            "TypedNavigation" => ($$"""
                { runtime { assetRepositoryIntegrationTestCar(first: 50) { items { rtId
                    nested: children(ckTypeIds: ["{{ReadingCkTypeId}}"]{{withComma}}) { {{selection}} } } } } }
                """, "data.runtime.assetRepositoryIntegrationTestCar.items", "nested"),
            "GenericOfTypedEntity" => ($$"""
                { runtime { assetRepositoryIntegrationTestCar(first: 50) { items { rtId
                    nested: associations(roleId: "System/ParentChild", direction: INBOUND, ckId: "{{ReadingCkTypeId}}"{{withComma}}) { {{selection}} } } } } }
                """, "data.runtime.assetRepositoryIntegrationTestCar.items", "nested"),
            "GenericTargets" => ($$"""
                { runtime { runtimeEntities(ckId: "{{CarCkTypeId}}", first: 50) { items { rtId
                    associations { nested: targets(roleId: "System/ParentChild", direction: INBOUND, ckId: "{{ReadingCkTypeId}}"{{withComma}}) { {{selection}} } } } } } }
                """, "data.runtime.runtimeEntities.items", "associations.nested"),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };

        var result = await _fixture.ExecuteGraphQlAsync(query);
        var answer = JObject.Parse(_fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(answer.ToString());

        var items = (JArray)answer.SelectToken(itemsPath)!;
        items.Should().NotBeEmpty(answer.ToString());
        return items.ToDictionary(i => i["rtId"]!.Value<string>()!,
            i => i.SelectToken($"{connectionPath}.totalCount")!.Value<int>());
    }
}
