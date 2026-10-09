using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     CK v2 (AB#5667): a CK interface (<c>AssetRepositoryIntegrationTest/Labeled-1</c>)
///     becomes a GraphQL interface without suffix, carrying the system fields and its members (optional members are
///     nullable), and every implementing concrete type lists it.
/// </summary>
[Collection(GraphQlCollection.Name)]
public class CkInterfaceIntrospectionTests
{
    private const string InterfaceName = "AssetRepositoryIntegrationTestLabeled";

    private readonly GraphQlTestFixture _fixture;

    public CkInterfaceIntrospectionTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task CkInterface_IsAnInterfaceWithMembersAndPossibleTypes()
    {
        var type = await TypeAsync(InterfaceName,
            "kind fields { name type { kind name ofType { name } } } possibleTypes { name }");

        type["kind"]!.Value<string>().Should().Be("INTERFACE");

        var fields = type["fields"]!.ToDictionary(f => f["name"]!.Value<string>()!, f => f["type"]!);
        fields.Keys.Should().Contain(["rtId", "ckTypeId", "name", "label"]);
        fields["name"]["kind"]!.Value<string>().Should().Be("NON_NULL");
        fields["label"]["kind"]!.Value<string>().Should().Be("SCALAR", "optional members are nullable");

        type["possibleTypes"]!.Select(t => t["name"]!.Value<string>()).Should().BeEquivalentTo(
            "AssetRepositoryIntegrationTestAccessTestAccount", "AssetRepositoryIntegrationTestAccessTestGroup");
    }

    [Theory]
    [InlineData("AssetRepositoryIntegrationTestAccessTestAccount")]
    [InlineData("AssetRepositoryIntegrationTestAccessTestGroup")]
    public async Task ImplementingType_ListsTheCkInterface(string typeName)
    {
        var type = await TypeAsync(typeName, "interfaces { name }");

        type["interfaces"]!.Select(i => i["name"]!.Value<string>()).Should().Contain(InterfaceName);
    }

    [Fact]
    public async Task NonImplementingType_DoesNotListTheCkInterface()
    {
        var type = await TypeAsync("AssetRepositoryIntegrationTestCustomer", "interfaces { name }");

        type["interfaces"]!.Select(i => i["name"]!.Value<string>()).Should().NotContain(InterfaceName);
    }

    [Fact]
    public async Task Fragment_OnCkInterface_SelectsMembersOfImplementingTypes()
    {
        var create = await _fixture.ExecuteGraphQlAsync("""
            mutation {
              runtime {
                runtimeEntities {
                  create(entities: [{ ckTypeId: "AssetRepositoryIntegrationTest/AccessTestGroup",
                                      attributes: [{ attributeName: "name", value: "fragment-group" }] }]) { rtId }
                }
              }
            }
            """);
        create.Errors.Should().BeNullOrEmpty();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query {
              runtime {
                assetRepositoryIntegrationTestAccessTestGroup(first: 50) {
                  items { ... on {{InterfaceName}} { name label } }
                }
              }
            }
            """);

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        json.Should().Contain("fragment-group");
        // The optional member Label is not assigned by AccessTestGroup: it is a nullable field that resolves to null.
        JObject.Parse(json).SelectTokens("data.runtime.assetRepositoryIntegrationTestAccessTestGroup.items[*].label")
            .Should().OnlyContain(t => t.Type == JTokenType.Null);
    }

    private const string ChildInterfaceName = "AssetRepositoryIntegrationTestMember";

    [Fact]
    public async Task ExtendingInterface_ImplementsItsParent_AndCarriesInheritedAndAssociationMembers()
    {
        // F1.5-S2 (AB#5921): Member-1 extends Labeled-1 and declares the AccessTestMembership association.
        var type = await TypeAsync(ChildInterfaceName,
            "kind interfaces { name } fields { name } possibleTypes { name }");

        type["kind"]!.Value<string>().Should().Be("INTERFACE");
        type["interfaces"]!.Select(i => i["name"]!.Value<string>()).Should().Equal(InterfaceName);
        type["fields"]!.Select(f => f["name"]!.Value<string>()).Should()
            .Contain(["name", "label", "memberOf"], "inherited members and the association member are fields");
        type["possibleTypes"]!.Select(t => t["name"]!.Value<string>()).Should()
            .BeEquivalentTo("AssetRepositoryIntegrationTestAccessTestAccount");
    }

    [Fact]
    public async Task ImplementingType_ListsTheFullInterfaceClosure()
    {
        var type = await TypeAsync("AssetRepositoryIntegrationTestAccessTestAccount", "interfaces { name }");

        type["interfaces"]!.Select(i => i["name"]!.Value<string>()).Should()
            .Contain([ChildInterfaceName, InterfaceName]);
    }

    [Fact]
    public async Task Fragment_OnExtendingInterface_ResolvesTheAssociationField()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccount(first: 5) {
                  items { ... on {{ChildInterfaceName}} { name memberOf(ckTypeIds: ["AssetRepositoryIntegrationTest/AccessTestGroup"]) { totalCount } } }
                }
              }
            }
            """);

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
    }

    [Fact]
    public async Task AssociationNarrowedToAnInterface_OnlyOffersImplementingTypes()
    {
        // F1.1-S5: AccessTestGroup.LinksToLabeled targets System/Entity narrowed to Labeled-1 implementors.
        var group = await TypeAsync("AssetRepositoryIntegrationTestAccessTestGroup",
            "fields { name type { name } }");
        var connectionName = group["fields"]!.Single(f => f["name"]!.Value<string>() == "linksToLabeled")
            .SelectToken("type.name")!.Value<string>()!;

        var connection = await TypeAsync(connectionName, "fields { name type { kind name ofType { name } } }");
        var unionName = connection["fields"]!.Single(f => f["name"]!.Value<string>() == "items")
            .SelectToken("type.ofType.name")!.Value<string>()!;
        var union = await TypeAsync(unionName, "possibleTypes { name }");

        union["possibleTypes"]!.Select(t => t["name"]!.Value<string>()).Should().BeEquivalentTo(
            "AssetRepositoryIntegrationTestAccessTestAccount", "AssetRepositoryIntegrationTestAccessTestGroup");
    }

    [Fact]
    public async Task InterfaceVersions_CoexistUnderDistinctNames()
    {
        // Labeled-1 -> AssetRepositoryIntegrationTestLabeled, Labeled-2 -> AssetRepositoryIntegrationTestLabeled2.
        (await TypeAsync(InterfaceName, "kind"))["kind"]!.Value<string>().Should().Be("INTERFACE");
        (await TypeAsync(InterfaceName + "2", "kind"))["kind"]!.Value<string>().Should().Be("INTERFACE");
    }

    private async Task<JToken> TypeAsync(string typeName, string selection)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""query { __type(name: "{{typeName}}") { {{selection}} } }""");
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        var type = JObject.Parse(json).SelectToken("data.__type");
        type.Should().NotBeNull(json);
        type!.Type.Should().NotBe(JTokenType.Null, $"type {typeName} must exist: {json}");
        return type;
    }
}
