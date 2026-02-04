using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for verifying that sortOrder works correctly on association queries (children, parent).
///
/// Bug: sortOrder on children associations is not applied, resulting in
/// unsorted results even when explicitly specifying a sort order.
///
/// Test data uses Car with VehicleReadings:
/// - Car BMW 320i (CarSalzburgABC123) has 2 children VehicleReadings:
///   - ReadingBMW320i_001 with readingValue 45678.5, readingTimestamp 2024-01-15T10:30:00Z
///   - ReadingBMW320i_002 with readingValue 75.0, readingTimestamp 2024-01-15T10:35:00Z
/// </summary>
[Collection("Sequential")]
public class AssociationSortOrderTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public AssociationSortOrderTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that sortOrder ASCENDING on children association is correctly applied.
    /// Queries Car children (VehicleReadings) sorted by readingValue ASCENDING.
    /// Expected order: 75.0, 45678.5 (smaller values first)
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_SortOrderAscending_ReturnsCorrectOrder()
    {
        // Arrange - Query BMW 320i which has 2 VehicleReadings with different readingValue values
        // VehicleReading with readingValue 75.0 should come before readingValue 45678.5
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""readingValue""
                          sortOrder: ASCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert - Query should execute without errors
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract readingValue values in order
        var readingValues = children!.Select(c => c["readingValue"]?.Value<double>()).ToList();
        _fixture.OutputHelper?.WriteLine($"ReadingValues in result order: {string.Join(", ", readingValues)}");

        // Verify ASCENDING order: 75.0 should come before 45678.5
        readingValues.Should().BeInAscendingOrder(
            "Children should be sorted by readingValue ASCENDING");
        readingValues[0].Should().Be(75.0, "First item should have the lowest readingValue");
        readingValues[1].Should().Be(45678.5, "Second item should have the higher readingValue");
    }

    /// <summary>
    /// Test that sortOrder DESCENDING on children association is correctly applied.
    /// Queries Car children (VehicleReadings) sorted by readingValue DESCENDING.
    /// Expected order: 45678.5, 75.0 (larger values first)
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_SortOrderDescending_ReturnsCorrectOrder()
    {
        // Arrange - Query BMW 320i which has 2 VehicleReadings
        // VehicleReading with readingValue 45678.5 should come before readingValue 75.0
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""readingValue""
                          sortOrder: DESCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract readingValue values in order
        var readingValues = children!.Select(c => c["readingValue"]?.Value<double>()).ToList();
        _fixture.OutputHelper?.WriteLine($"ReadingValues in result order: {string.Join(", ", readingValues)}");

        // Verify DESCENDING order: 45678.5 should come before 75.0
        readingValues.Should().BeInDescendingOrder(
            "Children should be sorted by readingValue DESCENDING");
        readingValues[0].Should().Be(45678.5, "First item should have the highest readingValue");
        readingValues[1].Should().Be(75.0, "Second item should have the lower readingValue");
    }

    /// <summary>
    /// Test that sortOrder on children works with string attributes (rtWellKnownName).
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_SortByStringAttribute_ReturnsCorrectOrder()
    {
        // Arrange - Query BMW 320i and sort children by rtWellKnownName ASCENDING
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""rtWellKnownName""
                          sortOrder: ASCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract rtWellKnownName values in order
        var wellKnownNames = children!.Select(c => c["rtWellKnownName"]?.Value<string>()).ToList();
        _fixture.OutputHelper?.WriteLine($"WellKnownNames in result order: {string.Join(", ", wellKnownNames)}");

        // Verify ASCENDING alphabetical order: ReadingBMW320i_001 should come before ReadingBMW320i_002
        wellKnownNames.Should().BeInAscendingOrder(
            "Children should be sorted by rtWellKnownName ASCENDING");
        wellKnownNames[0].Should().Be("ReadingBMW320i_001", "First item should be ReadingBMW320i_001");
        wellKnownNames[1].Should().Be("ReadingBMW320i_002", "Second item should be ReadingBMW320i_002");
    }

    /// <summary>
    /// Test that sortOrder works with string attributes in DESCENDING order.
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_SortByStringAttributeDescending_ReturnsCorrectOrder()
    {
        // Arrange - Query BMW 320i and sort children by rtWellKnownName DESCENDING
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    name
                    children(
                      first: 100,
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
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract rtWellKnownName values in order
        var wellKnownNames = children!.Select(c => c["rtWellKnownName"]?.Value<string>()).ToList();
        _fixture.OutputHelper?.WriteLine($"WellKnownNames in result order: {string.Join(", ", wellKnownNames)}");

        // Verify DESCENDING alphabetical order: ReadingBMW320i_002 should come before ReadingBMW320i_001
        wellKnownNames.Should().BeInDescendingOrder(
            "Children should be sorted by rtWellKnownName DESCENDING");
        wellKnownNames[0].Should().Be("ReadingBMW320i_002", "First item should be ReadingBMW320i_002");
        wellKnownNames[1].Should().Be("ReadingBMW320i_001", "Second item should be ReadingBMW320i_001");
    }

    /// <summary>
    /// Test that sortOrder works on interface associations (queried via VehicleInterface).
    /// This ensures the fix for interface associations also properly applies sorting.
    /// </summary>
    [Fact]
    public async Task GraphQL_InterfaceChildrenAssociation_SortOrder_ReturnsCorrectOrder()
    {
        // Arrange - Query all Vehicles (via interface) and sort their VehicleReading children
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    __typename
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""readingValue""
                          sortOrder: ASCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var vehicles = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicle.items") as JArray;
        vehicles.Should().NotBeNull();
        vehicles.Should().HaveCount(1);

        var vehicle = vehicles![0];
        var children = vehicle.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract readingValue values in order
        var readingValues = children!.Select(c => c["readingValue"]?.Value<double>()).ToList();
        _fixture.OutputHelper?.WriteLine($"ReadingValues in result order: {string.Join(", ", readingValues)}");

        // Verify ASCENDING order: 75.0 should come before 45678.5
        readingValues.Should().BeInAscendingOrder(
            "Children should be sorted by readingValue ASCENDING");
        readingValues[0].Should().Be(75.0, "First item should have the lowest readingValue (75.0)");
        readingValues[1].Should().Be(45678.5, "Second item should have the higher readingValue (45678.5)");
    }

    /// <summary>
    /// Test that sortOrder without any criteria returns results (default MongoDB order).
    /// This is a baseline test to verify queries work without sortOrder.
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_NoSortOrder_ReturnsResults()
    {
        // Arrange - Query without sortOrder to verify baseline behavior
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""]
                    ) {
                      totalCount
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
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

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Just verify we got results - no specific order expected
        var readingValues = children!.Select(c => c["readingValue"]?.Value<double>()).ToList();
        _fixture.OutputHelper?.WriteLine($"ReadingValues without sort: {string.Join(", ", readingValues)}");
        readingValues.Should().Contain(75.0);
        readingValues.Should().Contain(45678.5);
    }

    /// <summary>
    /// Test that sortOrder works on nested attribute paths (e.g., "TimeRange.From").
    /// This tests the scenario from the original bug report with nested properties.
    /// </summary>
    [Fact]
    public async Task GraphQL_ChildrenAssociation_SortByNestedAttributePath_ExecutesWithoutError()
    {
        // Arrange - Query VehicleReadings sorted by readingTimestamp (simulating nested path behavior)
        // Note: Our test data uses readingTimestamp at root level, but this tests the general mechanism
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCar(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: ""CarSalzburgABC123""
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    children(
                      first: 100,
                      ckTypeIds: [""AssetRepositoryIntegrationTest/VehicleReading""],
                      sortOrder: [
                        {
                          attributePath: ""readingTimestamp""
                          sortOrder: ASCENDING
                        }
                      ]
                    ) {
                      totalCount
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
                          rtWellKnownName
                          readingValue
                          readingTimestamp
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
            $"Query with nested attribute path sortOrder should succeed. " +
            $"Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var cars = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        cars.Should().NotBeNull();
        cars.Should().HaveCount(1);

        var car = cars![0];
        var children = car.SelectToken("children.items") as JArray;
        children.Should().NotBeNull();
        children.Should().HaveCount(2, "BMW 320i has 2 VehicleReadings");

        // Extract timestamps and verify they're sorted
        var timestamps = children!.Select(c => c["readingTimestamp"]?.Value<DateTime>()).ToList();
        _fixture.OutputHelper?.WriteLine($"Timestamps in result order: {string.Join(", ", timestamps)}");

        // Verify ASCENDING order for timestamps
        timestamps.Should().BeInAscendingOrder(
            "Children should be sorted by readingTimestamp ASCENDING");
    }
}
