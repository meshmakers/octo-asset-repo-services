using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Regression tests for AB#4532: the batch loader cache key of associations.definitions
/// did not include roleId (and other arguments), so the arguments of the FIRST definitions
/// selection were silently applied to ALL aliased definitions selections of the same
/// entity type and direction.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class GenericAssociationDefinitionsAliasTests
{
    // Car 1 (CarSalzburgABC123) has exactly one VehicleOwnership and one ParentChild
    // outbound association, and no Ownership association.
    private const string Car1RtId = "67000004dddd4444eeee0001";

    private readonly GraphQlTestFixture _fixture;

    public GenericAssociationDefinitionsAliasTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Two aliased definitions selections with different roleIds must each be resolved
    /// with their own arguments. The selection with an existing association comes first,
    /// so before the fix the second (non-matching) selection wrongly returned 1.
    /// </summary>
    [Fact]
    public async Task GraphQL_AliasedDefinitions_DifferentRoleIds_EachUsesOwnArguments()
    {
        var query = $$"""
            query {
              runtime {
                runtimeEntities(ckId: "AssetRepositoryIntegrationTest/Car", rtId: "{{Car1RtId}}") {
                  items {
                    rtId
                    associations {
                      vehicleOwnership: definitions(direction: OUTBOUND, roleId: "AssetRepositoryIntegrationTest/VehicleOwnership", first: 10) {
                        totalCount
                        items { ckAssociationRoleId }
                      }
                      ownership: definitions(direction: OUTBOUND, roleId: "AssetRepositoryIntegrationTest/Ownership", first: 10) {
                        totalCount
                      }
                      parentChild: definitions(direction: OUTBOUND, roleId: "System/ParentChild", first: 10) {
                        totalCount
                        items { ckAssociationRoleId }
                      }
                    }
                  }
                }
              }
            }
            """;

        var result = await _fixture.ExecuteGraphQlAsync(query);

        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var associations = answer.SelectToken("data.runtime.runtimeEntities.items[0].associations");
        associations.Should().NotBeNull();

        associations!.SelectToken("vehicleOwnership.totalCount")?.Value<int>().Should().Be(1);
        associations.SelectToken("vehicleOwnership.items[0].ckAssociationRoleId")?.Value<string>()
            .Should().Contain("VehicleOwnership");

        associations.SelectToken("ownership.totalCount")?.Value<int>()
            .Should().Be(0, "Car 1 has no Ownership association; before AB#4532 the first selection's roleId leaked into this selection");

        associations.SelectToken("parentChild.totalCount")?.Value<int>().Should().Be(1);
        associations.SelectToken("parentChild.items[0].ckAssociationRoleId")?.Value<string>()
            .Should().Contain("ParentChild");
    }

    /// <summary>
    /// Same as above with the non-matching selection first — before the fix this order
    /// made every selection return 0.
    /// </summary>
    [Fact]
    public async Task GraphQL_AliasedDefinitions_NonMatchingRoleFirst_EachUsesOwnArguments()
    {
        var query = $$"""
            query {
              runtime {
                runtimeEntities(ckId: "AssetRepositoryIntegrationTest/Car", rtId: "{{Car1RtId}}") {
                  items {
                    associations {
                      ownership: definitions(direction: OUTBOUND, roleId: "AssetRepositoryIntegrationTest/Ownership", first: 10) {
                        totalCount
                      }
                      vehicleOwnership: definitions(direction: OUTBOUND, roleId: "AssetRepositoryIntegrationTest/VehicleOwnership", first: 10) {
                        totalCount
                      }
                    }
                  }
                }
              }
            }
            """;

        var result = await _fixture.ExecuteGraphQlAsync(query);

        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var associations = answer.SelectToken("data.runtime.runtimeEntities.items[0].associations");
        associations.Should().NotBeNull();

        associations!.SelectToken("ownership.totalCount")?.Value<int>().Should().Be(0);
        associations.SelectToken("vehicleOwnership.totalCount")?.Value<int>()
            .Should().Be(1, "the second selection must not inherit the first selection's roleId (AB#4532)");
    }
}
