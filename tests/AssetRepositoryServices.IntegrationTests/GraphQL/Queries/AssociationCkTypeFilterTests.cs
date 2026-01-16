using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for filtering associations by ckTypeId and ckTypeIds arguments.
/// These tests verify that:
/// 1. ckTypeId can accept a base/abstract type and return all derived types
/// 2. ckTypeIds can accept a list of type IDs for filtering
/// </summary>
[Collection("Sequential")]
public class AssociationCkTypeFilterTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public AssociationCkTypeFilterTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that filtering by a specific concrete type (Car) returns only entities of that type.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeId_SpecificType_ReturnsOnlyThatType()
    {
        // Arrange - Query vehicles filtered to only Car type
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
                    ownsVehicles(first: 10, ckTypeId: ""AssetRepositoryIntegrationTest/Car"") {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestCar {
                          rtId
                          rtWellKnownName
                          name
                          numberOfDoors
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
        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();
        customers.Should().HaveCount(1);

        var customer = customers![0];
        var vehicles = customer.SelectToken("ownsVehicles.items") as JArray;
        vehicles.Should().NotBeNull();
        vehicles.Should().HaveCount(1, "Max Mustermann owns one Car");

        var vehicle = vehicles![0];
        vehicle["__typename"]?.Value<string>().Should().Be("AssetRepositoryIntegrationTestCar");
        vehicle["numberOfDoors"]?.Value<int>().Should().Be(4);
    }

    /// <summary>
    /// Test that filtering by the abstract base type (Vehicle) returns all derived types (Car and Truck).
    /// This is the new functionality - passing a base type should query all derived types.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeId_BaseType_ReturnsAllDerivedTypes()
    {
        // Arrange - Query all vehicles (Cars and Trucks) by using the abstract base type Vehicle
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeId: ""AssetRepositoryIntegrationTest/Vehicle"") {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleInterface {
                          rtId
                          rtWellKnownName
                          name
                          licensePlate
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
        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();
        customers.Should().HaveCount(4);

        // Count total vehicles and verify we get both Cars and Trucks
        var allVehicles = new List<JToken>();
        var typeNames = new HashSet<string>();

        foreach (var customer in customers!)
        {
            var vehicles = customer.SelectToken("ownsVehicles.items") as JArray;
            if (vehicles != null)
            {
                foreach (var vehicle in vehicles)
                {
                    allVehicles.Add(vehicle);
                    var typeName = vehicle["__typename"]?.Value<string>();
                    if (typeName != null)
                    {
                        typeNames.Add(typeName);
                    }
                }
            }
        }

        allVehicles.Should().HaveCount(4, "There should be 4 vehicles total (2 cars + 2 trucks)");
        typeNames.Should().Contain("AssetRepositoryIntegrationTestCar", "Should contain Car entities");
        typeNames.Should().Contain("AssetRepositoryIntegrationTestTruck", "Should contain Truck entities");
    }

    /// <summary>
    /// Test that filtering by ckTypeIds with a single type works the same as ckTypeId.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeIds_SingleType_ReturnsOnlyThatType()
    {
        // Arrange - Query vehicles filtered to only Truck type using ckTypeIds (list)
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CustomerTechGmbH""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Truck""]) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestTruck {
                          rtId
                          rtWellKnownName
                          name
                          loadCapacity
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
        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();
        customers.Should().HaveCount(1);

        var customer = customers![0];
        var vehicles = customer.SelectToken("ownsVehicles.items") as JArray;
        vehicles.Should().NotBeNull();
        vehicles.Should().HaveCount(1, "Tech GmbH owns one Truck");

        var vehicle = vehicles![0];
        vehicle["__typename"]?.Value<string>().Should().Be("AssetRepositoryIntegrationTestTruck");
        vehicle["loadCapacity"]?.Value<double>().Should().Be(3500.0);
    }

    /// <summary>
    /// Test that filtering by ckTypeIds with multiple specific types returns entities of those types.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeIds_MultipleTypes_ReturnsSpecifiedTypes()
    {
        // Arrange - Query all customers and filter vehicles to both Car and Truck types
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Car"", ""AssetRepositoryIntegrationTest/Truck""]) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestCar {
                          rtId
                          rtWellKnownName
                          name
                          numberOfDoors
                        }
                        ... on AssetRepositoryIntegrationTestTruck {
                          rtId
                          rtWellKnownName
                          name
                          loadCapacity
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
        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();

        // Count total vehicles across all customers
        var allVehicles = new List<JToken>();
        var typeNames = new HashSet<string>();

        foreach (var customer in customers!)
        {
            var vehicles = customer.SelectToken("ownsVehicles.items") as JArray;
            if (vehicles != null)
            {
                foreach (var vehicle in vehicles)
                {
                    allVehicles.Add(vehicle);
                    var typeName = vehicle["__typename"]?.Value<string>();
                    if (typeName != null)
                    {
                        typeNames.Add(typeName);
                    }
                }
            }
        }

        allVehicles.Should().HaveCount(4, "There should be 4 vehicles total (2 cars + 2 trucks)");
        typeNames.Should().Contain("AssetRepositoryIntegrationTestCar");
        typeNames.Should().Contain("AssetRepositoryIntegrationTestTruck");
    }

    /// <summary>
    /// Test that filtering by ckTypeIds with only Car type returns only Cars across all customers.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeIds_OnlyCars_ReturnsOnlyCars()
    {
        // Arrange - Query all customers but only get their Cars (not Trucks)
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    ownsVehicles(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/Car""]) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestCar {
                          rtId
                          rtWellKnownName
                          name
                          numberOfDoors
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
        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();

        // Count only Car vehicles
        var allVehicles = new List<JToken>();
        foreach (var customer in customers!)
        {
            var vehicles = customer.SelectToken("ownsVehicles.items") as JArray;
            if (vehicles != null)
            {
                allVehicles.AddRange(vehicles);
            }
        }

        allVehicles.Should().HaveCount(2, "There should be 2 cars total");

        foreach (var vehicle in allVehicles)
        {
            vehicle["__typename"]?.Value<string>().Should().Be("AssetRepositoryIntegrationTestCar",
                "All returned vehicles should be Cars");
        }
    }

    /// <summary>
    /// Test that an invalid ckTypeId that doesn't exist throws an appropriate error.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeId_InvalidType_ReturnsError()
    {
        // Arrange - Query with a type ID that doesn't exist
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
                    ownsVehicles(first: 10, ckTypeId: ""NonExistent/Type"") {
                      totalCount
                      items {
                        __typename
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
        result.Errors.Should().NotBeNullOrEmpty("An error should be returned for an invalid type");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // The error message indicates the type was not found in the CK cache
        var errorMessage = result.Errors?.FirstOrDefault()?.InnerException?.Message
            ?? result.Errors?.FirstOrDefault()?.Message;
        (errorMessage?.Contains("not found") == true ||
         errorMessage?.Contains("does not exist") == true ||
         errorMessage?.Contains("not allowed") == true ||
         errorMessage?.Contains("no derived types") == true)
            .Should().BeTrue($"Error should indicate the type doesn't exist or is not allowed. Actual message: {errorMessage}");
    }

    /// <summary>
    /// Test that ckTypeId with a type that is not related to the association throws an error.
    /// For example, passing a Customer type ID when the association expects Vehicle types.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeId_UnrelatedType_ReturnsError()
    {
        // Arrange - Query vehicles with Customer type (which is not a valid target for ownsVehicles)
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
                    ownsVehicles(first: 10, ckTypeId: ""AssetRepositoryIntegrationTest/Customer"") {
                      totalCount
                      items {
                        __typename
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
        result.Errors.Should().NotBeNullOrEmpty("An error should be returned for an unrelated type");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // The error message indicates the type has no derived types in the allowed list
        var errorMessage = result.Errors?.FirstOrDefault()?.InnerException?.Message
            ?? result.Errors?.FirstOrDefault()?.Message;
        errorMessage.Should().Contain("no derived types that are allowed",
            "Error should indicate the type has no derived types in the allowed list");
    }

    /// <summary>
    /// Test that ckTypeId with a base type that has no derived types in the allowed list throws an error.
    /// This tests the validation that checks if the base type has any allowed derived types.
    /// </summary>
    [Fact]
    public async Task GraphQL_AssociationWithCkTypeId_BaseTypeWithNoAllowedDerived_ReturnsError()
    {
        // Arrange - Query vehicles with OperatingFacility type (which has no derived types in the Vehicle association)
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
                    ownsVehicles(first: 10, ckTypeId: ""AssetRepositoryIntegrationTest/OperatingFacility"") {
                      totalCount
                      items {
                        __typename
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
        result.Errors.Should().NotBeNullOrEmpty("An error should be returned for a type with no allowed derived types");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");
    }
}
