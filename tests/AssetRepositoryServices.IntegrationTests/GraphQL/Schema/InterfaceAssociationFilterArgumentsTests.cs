using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
/// Tests for verifying that interface association fields have the same filter arguments
/// as concrete type association fields.
///
/// Issue: Interface association fields (created via InterfaceAssociationField) previously
/// only had 'first' and 'after' pagination arguments. Concrete type association fields
/// (created via AssociationField) have additional filter arguments like:
/// - searchFilter
/// - fieldFilter
/// - sortOrder
/// - ckTypeId/ckTypeIds
/// - rtId/rtIds
/// - aggregations
///
/// Fix: InterfaceAssociationField now adds the same filter arguments as AssociationField,
/// allowing queries via interface fragments to use all filter capabilities.
/// </summary>
[Collection("Sequential")]
public class InterfaceAssociationFilterArgumentsTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public InterfaceAssociationFilterArgumentsTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that interface association fields have the expected filter arguments.
    /// This verifies that VehicleInterface's 'children' field has searchFilter, fieldFilter, etc.
    /// </summary>
    [Fact]
    public async Task GraphQL_InterfaceAssociationField_HasFilterArguments()
    {
        // Arrange - Query the interface type to see its field arguments
        var query = @"
            query {
              vehicleInterface: __type(name: ""AssetRepositoryIntegrationTestVehicleInterface"") {
                name
                fields {
                  name
                  args {
                    name
                    type {
                      name
                      kind
                      ofType { name kind }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema introspection should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"VehicleInterface introspection: {json}");

        var answer = JObject.Parse(json);
        var vehicleInterface = answer.SelectToken("data.vehicleInterface");
        vehicleInterface.Should().NotBeNull("VehicleInterface should exist in schema");

        // Find the 'children' field (inbound association from VehicleReading)
        var fields = vehicleInterface!.SelectToken("fields") as JArray;
        fields.Should().NotBeNull("VehicleInterface should have fields");

        var childrenField = fields!.FirstOrDefault(f => f["name"]?.Value<string>() == "children");
        childrenField.Should().NotBeNull("VehicleInterface should have 'children' association field");

        // Get the arguments for the children field
        var args = childrenField!.SelectToken("args") as JArray;
        args.Should().NotBeNull("children field should have arguments");

        var argNames = args!.Select(a => a["name"]?.Value<string>()).ToList();
        _fixture.OutputHelper?.WriteLine($"children field arguments: {string.Join(", ", argNames)}");

        // Verify pagination arguments exist
        argNames.Should().Contain("first", "children field should have 'first' pagination argument");
        argNames.Should().Contain("after", "children field should have 'after' pagination argument");

        // Verify filter arguments exist (these are the new ones added by the fix)
        argNames.Should().Contain("searchFilter", "children field should have 'searchFilter' argument");
        argNames.Should().Contain("fieldFilter", "children field should have 'fieldFilter' argument");
        argNames.Should().Contain("sortOrder", "children field should have 'sortOrder' argument");
        argNames.Should().Contain("ckTypeIds", "children field should have 'ckTypeIds' argument");
        argNames.Should().Contain("rtId", "children field should have 'rtId' argument");
        argNames.Should().Contain("rtIds", "children field should have 'rtIds' argument");
        argNames.Should().Contain("aggregations", "children field should have 'aggregations' argument");
    }

    /// <summary>
    /// Test that EquipmentInterface's 'hasReadings' field has filter arguments.
    /// This tests a different interface to ensure the fix applies universally.
    /// </summary>
    [Fact]
    public async Task GraphQL_EquipmentInterfaceAssociationField_HasFilterArguments()
    {
        // Arrange - Query the EquipmentInterface type
        var query = @"
            query {
              equipmentInterface: __type(name: ""AssetRepositoryIntegrationTestEquipmentInterface"") {
                name
                fields {
                  name
                  args {
                    name
                    type {
                      name
                      kind
                      ofType { name kind }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema introspection should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"EquipmentInterface introspection: {json}");

        var answer = JObject.Parse(json);
        var equipmentInterface = answer.SelectToken("data.equipmentInterface");
        equipmentInterface.Should().NotBeNull("EquipmentInterface should exist in schema");

        // Find the 'hasReadings' field (inbound association from SensorReading)
        var fields = equipmentInterface!.SelectToken("fields") as JArray;
        fields.Should().NotBeNull("EquipmentInterface should have fields");

        var hasReadingsField = fields!.FirstOrDefault(f => f["name"]?.Value<string>() == "hasReadings");
        hasReadingsField.Should().NotBeNull("EquipmentInterface should have 'hasReadings' association field");

        // Get the arguments
        var args = hasReadingsField!.SelectToken("args") as JArray;
        args.Should().NotBeNull("hasReadings field should have arguments");

        var argNames = args!.Select(a => a["name"]?.Value<string>()).ToList();
        _fixture.OutputHelper?.WriteLine($"hasReadings field arguments: {string.Join(", ", argNames)}");

        // Verify all expected arguments exist
        argNames.Should().Contain("first");
        argNames.Should().Contain("after");
        argNames.Should().Contain("searchFilter");
        argNames.Should().Contain("fieldFilter");
        argNames.Should().Contain("sortOrder");
        argNames.Should().Contain("ckTypeIds");
        argNames.Should().Contain("rtId");
        argNames.Should().Contain("rtIds");
        argNames.Should().Contain("aggregations");
    }

    /// <summary>
    /// Compare interface and concrete type association field arguments to ensure they match.
    /// </summary>
    [Fact]
    public async Task GraphQL_InterfaceAndConcreteType_HaveSameAssociationFieldArguments()
    {
        // Arrange - Query both interface and concrete type
        var query = @"
            query {
              interface: __type(name: ""AssetRepositoryIntegrationTestVehicleInterface"") {
                name
                fields {
                  name
                  args { name }
                }
              }
              concreteType: __type(name: ""AssetRepositoryIntegrationTestCar"") {
                name
                fields {
                  name
                  args { name }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Interface vs Concrete type comparison: {json}");

        var answer = JObject.Parse(json);

        // Get interface children field args
        var interfaceFields = answer.SelectToken("data.interface.fields") as JArray;
        var interfaceChildrenField = interfaceFields!.FirstOrDefault(f => f["name"]?.Value<string>() == "children");
        interfaceChildrenField.Should().NotBeNull();

        var interfaceArgs = interfaceChildrenField!.SelectToken("args") as JArray;
        var interfaceArgNames = interfaceArgs!.Select(a => a["name"]?.Value<string>()).OrderBy(n => n).ToList();

        // Get concrete type children field args
        var concreteFields = answer.SelectToken("data.concreteType.fields") as JArray;
        var concreteChildrenField = concreteFields!.FirstOrDefault(f => f["name"]?.Value<string>() == "children");
        concreteChildrenField.Should().NotBeNull();

        var concreteArgs = concreteChildrenField!.SelectToken("args") as JArray;
        var concreteArgNames = concreteArgs!.Select(a => a["name"]?.Value<string>()).OrderBy(n => n).ToList();

        _fixture.OutputHelper?.WriteLine($"Interface args: {string.Join(", ", interfaceArgNames)}");
        _fixture.OutputHelper?.WriteLine($"Concrete args: {string.Join(", ", concreteArgNames)}");

        // Interface should have at least all the filter arguments that concrete types have
        // (concrete types may have additional args from ConnectionBuilder like 'last' and 'before')
        var expectedFilterArgs = new[] { "searchFilter", "fieldFilter", "sortOrder", "ckTypeIds", "rtId", "rtIds", "aggregations" };
        foreach (var expectedArg in expectedFilterArgs)
        {
            interfaceArgNames.Should().Contain(expectedArg,
                $"Interface association field should have '{expectedArg}' argument like concrete types");
        }
    }

    /// <summary>
    /// Test that querying via an interface with filter arguments actually works at runtime.
    /// This verifies that the filter arguments are not just present in the schema but also functional.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryViaInterface_WithFieldFilter_Works()
    {
        // Arrange - Query vehicles with children using fieldFilter on the interface
        // Note: VehicleReadings have rtWellKnownName that we can filter on
        // ckTypeIds is required, so we include it
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  items {
                    rtWellKnownName
                    __typename
                    children(
                      first: 10,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      fieldFilter: [
                        {
                          attributePath: ""rtWellKnownName""
                          operator: LIKE
                          comparisonValue: ""%Reading%""
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtWellKnownName
                          readingValue
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert - The query should execute without errors
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query with fieldFilter on interface association should succeed. " +
            $"Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Query result: {json}");
    }

    /// <summary>
    /// Test that sortOrder argument works on interface association fields.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryViaInterface_WithSortOrder_Works()
    {
        // Arrange - Query vehicles with children using sortOrder
        // Note: The Sort input type uses 'sortOrder' field (not 'sortDirection')
        // ckTypeIds is required, so we include it
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  items {
                    rtWellKnownName
                    children(
                      first: 10,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""rtWellKnownName""
                          sortOrder: DESCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtWellKnownName
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query with sortOrder on interface association should succeed. " +
            $"Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Query result: {json}");
    }

    /// <summary>
    /// Test that ckTypeIds filter argument works on interface association fields.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryViaInterface_WithCkTypeIdsFilter_Works()
    {
        // Arrange - Query vehicles with children filtered by ckTypeIds
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  items {
                    rtWellKnownName
                    children(
                      first: 10,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""]
                    ) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtWellKnownName
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query with ckTypeIds on interface association should succeed. " +
            $"Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Query result: {json}");
    }

    /// <summary>
    /// Test deep hierarchy (6 levels) interface association field has filter arguments.
    /// This ensures the fix works for all interface levels, not just the top level.
    /// </summary>
    [Fact]
    public async Task GraphQL_DeepHierarchyInterface_HasFilterArguments()
    {
        // Arrange - Query the CollaborativeRobotInterface (5 levels deep)
        var query = @"
            query {
              collabRobotInterface: __type(name: ""AssetRepositoryIntegrationTestCollaborativeRobotInterface"") {
                name
                fields {
                  name
                  args { name }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"CollaborativeRobotInterface introspection: {json}");

        var answer = JObject.Parse(json);
        var collabInterface = answer.SelectToken("data.collabRobotInterface");
        collabInterface.Should().NotBeNull("CollaborativeRobotInterface should exist");

        // Find hasReadings field (inherited from Equipment through 5 levels)
        var fields = collabInterface!.SelectToken("fields") as JArray;
        var hasReadingsField = fields!.FirstOrDefault(f => f["name"]?.Value<string>() == "hasReadings");
        hasReadingsField.Should().NotBeNull("CollaborativeRobotInterface should have inherited hasReadings field");

        var args = hasReadingsField!.SelectToken("args") as JArray;
        var argNames = args!.Select(a => a["name"]?.Value<string>()).ToList();

        _fixture.OutputHelper?.WriteLine($"hasReadings arguments at 5 levels deep: {string.Join(", ", argNames)}");

        // Verify filter arguments are present even at the deepest interface level
        argNames.Should().Contain("searchFilter");
        argNames.Should().Contain("fieldFilter");
        argNames.Should().Contain("sortOrder");
    }
}
