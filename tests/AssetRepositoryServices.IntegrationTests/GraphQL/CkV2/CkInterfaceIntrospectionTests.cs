using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     CK v2 Phase 0 (AB#5667, contract §4.2 / §8.3): a CK interface (<c>AssetRepositoryIntegrationTest/Labeled-1</c>)
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
