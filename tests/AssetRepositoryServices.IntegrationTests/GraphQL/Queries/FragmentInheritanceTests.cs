using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for fragment inheritance on union types.
/// Verifies that inline fragments on a base type match entities of derived types.
///
/// Bug: When querying an association that returns a union of derived types,
/// using a fragment on the abstract base type does not match the derived type entities.
/// For example, `... on Vehicle` should match both Car and Truck entities,
/// but currently only the specific type fragments work.
/// </summary>
[Collection("Sequential")]
public class FragmentInheritanceTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public FragmentInheritanceTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that querying vehicles with specific type fragments works correctly.
    /// This is the working case - using fragments on concrete derived types.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryCustomerVehicles_WithSpecificTypeFragments_ReturnsData()
    {
        // Arrange - Using specific type fragments for Car and Truck (the working approach)
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Vehicle""]) {
                      totalCount
                      edges {
                        node {
                          ... on AssetRepositoryIntegrationTestCar {
                            rtId
                            rtWellKnownName
                            name
                            licensePlate
                            year
                            numberOfDoors
                          }
                          ... on AssetRepositoryIntegrationTestTruck {
                            rtId
                            rtWellKnownName
                            name
                            licensePlate
                            year
                            loadCapacity
                          }
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestCustomer.items should exist");

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(4); // 4 active customers

        // Count total vehicles across all customers
        var totalVehicles = 0;
        foreach (var customer in customersArray)
        {
            var vehicleEdges = customer.SelectToken("ownsVehicles.edges");
            vehicleEdges.Should().NotBeNull();

            var vehicles = (JArray)vehicleEdges;
            totalVehicles += vehicles.Count;

            // Verify that returned vehicles have data (not empty due to fragment mismatch)
            foreach (var edge in vehicles)
            {
                var node = edge["node"];
                node.Should().NotBeNull();

                // At least rtId should be present
                var rtId = node["rtId"];
                rtId.Should().NotBeNull("Vehicle node should have rtId from the matching fragment");
            }
        }

        totalVehicles.Should().Be(4, "There should be 4 vehicles total (2 cars + 2 trucks)");
    }

    /// <summary>
    /// Test that querying vehicles with a base type fragment should work.
    /// This is the BUG case - using a fragment on the abstract base type Vehicle
    /// should match both Car and Truck entities, but currently does not work.
    ///
    /// The fragment `... on AssetRepositoryIntegrationTestVehicle` should match
    /// entities of type Car and Truck because they derive from Vehicle.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryCustomerVehicles_WithBaseTypeFragment_ShouldMatchDerivedTypes()
    {
        // Arrange - Using base type interface fragment (VehicleInterface) which should match Car and Truck
        // The interface is created for abstract types with suffix "Interface" to avoid name collision
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Vehicle""]) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleInterface {
                          rtId
                          rtWellKnownName
                          name
                          licensePlate
                          year
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();

        var customersArray = (JArray)customers;

        // Find Max Mustermann who owns a Car
        var maxMustermann = customersArray.FirstOrDefault(c =>
            c["rtWellKnownName"]?.Value<string>() == "CustomerMaxMustermann");
        maxMustermann.Should().NotBeNull("Max Mustermann should exist");

        var maxVehicles = maxMustermann.SelectToken("ownsVehicles.items");
        maxVehicles.Should().NotBeNull();

        var maxVehiclesArray = (JArray)maxVehicles;
        maxVehiclesArray.Should().HaveCount(1, "Max Mustermann should have 1 vehicle");

        var maxVehicle = maxVehiclesArray[0];

        // The __typename should be the actual derived type
        var typeName = maxVehicle["__typename"]?.Value<string>();
        typeName.Should().Be("AssetRepositoryIntegrationTestCar",
            "The actual type should be Car");

        // BUG: This is where the test fails - the fragment on Vehicle should match Car
        // but currently the fragment fields are not resolved because ResolveType
        // returns Car, and the fragment is on Vehicle (base type)
        var rtId = maxVehicle["rtId"];
        rtId.Should().NotBeNull(
            "The base type fragment on Vehicle should match the derived type Car. " +
            "Currently failing because RtEntityUnionType.ResolveType returns the exact type (Car) " +
            "but GraphQL fragment matching requires the fragment type name to match exactly.");

        var name = maxVehicle["name"];
        name.Should().NotBeNull("Vehicle fragment should provide the name field");
        name.Value<string>().Should().Be("BMW 320i");

        var licensePlate = maxVehicle["licensePlate"];
        licensePlate.Should().NotBeNull("Vehicle fragment should provide the licensePlate field");
        licensePlate.Value<string>().Should().Be("S-ABC123");
    }

    /// <summary>
    /// Test that demonstrates the workaround: using multiple specific type fragments
    /// to query common fields from the base type.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryCustomerVehicles_WorkaroundWithMultipleFragments_ReturnsData()
    {
        // Arrange - Workaround: define fragments for each derived type with common fields
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CustomerMaxMustermann""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Vehicle""]) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestCar {
                          rtId
                          rtWellKnownName
                          name
                          licensePlate
                          year
                        }
                        ... on AssetRepositoryIntegrationTestTruck {
                          rtId
                          rtWellKnownName
                          name
                          licensePlate
                          year
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(1);

        var maxMustermann = customersArray[0];
        var vehicles = maxMustermann.SelectToken("ownsVehicles.items");
        vehicles.Should().NotBeNull();

        var vehiclesArray = (JArray)vehicles;
        vehiclesArray.Should().HaveCount(1);

        var vehicle = vehiclesArray[0];

        // With the workaround, the specific type fragment matches
        var rtId = vehicle["rtId"];
        rtId.Should().NotBeNull("Specific type fragment should match and provide rtId");

        var name = vehicle["name"];
        name.Should().NotBeNull("Specific type fragment should provide the name field");
        name.Value<string>().Should().Be("BMW 320i");
    }

    /// <summary>
    /// Test that querying abstract types directly (via the abstract type's query endpoint) works.
    /// This verifies that abstract types still have query endpoints after adding interface support.
    /// The query endpoint for an abstract type should return all entities of derived types.
    /// Note: Inline fragments on derived types don't work in this context due to GraphQL validation,
    /// but the query endpoint should still return data with the base type's fields.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryAbstractVehicleType_ReturnsAllDerivedTypeEntities()
    {
        // Arrange - Query the abstract Vehicle type directly
        // This should return both Car and Truck entities
        // Note: We can only request fields from the base type (Vehicle), not derived type-specific fields
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  totalCount
                  items {
                    rtId
                    rtWellKnownName
                    name
                    licensePlate
                    year
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);

        var vehicles = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicle");
        vehicles.Should().NotBeNull("Query endpoint for abstract Vehicle type should exist");

        var totalCount = vehicles["totalCount"]?.Value<int>();
        totalCount.Should().Be(4, "There should be 4 vehicles (2 cars + 2 trucks)");

        var items = (JArray)vehicles["items"]!;
        items.Should().HaveCount(4);

        // Verify that all vehicles have the base type fields populated
        foreach (var vehicle in items)
        {
            vehicle["rtId"].Should().NotBeNull("All vehicles should have rtId");
            vehicle["name"].Should().NotBeNull("All vehicles should have name from base type");
            vehicle["licensePlate"].Should().NotBeNull("All vehicles should have licensePlate from base type");
        }

        // Verify we have vehicles with different well-known names (indicating both Cars and Trucks)
        var wellKnownNames = items.Select(v => v["rtWellKnownName"]?.Value<string>()).ToList();
        wellKnownNames.Should().Contain(n => n!.Contains("Car"), "Should contain Car entities");
        wellKnownNames.Should().Contain(n => n!.Contains("Truck"), "Should contain Truck entities");
    }
}
