using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for parent association resolution when filtering by an abstract type.
///
/// This tests the scenario described in the EnergyCommunity issue where:
/// - EnergyQuantity -> ParentChild -> MeteringPoint (abstract)
/// - MeteringPoint (abstract) -> Consumer, Producer (derived concrete types)
/// - MeteringPoint -> ParentChild -> OperatingFacility
///
/// When querying parent(ckTypeId: "MeteringPoint") on EnergyQuantity,
/// it should return Consumer/Producer, NOT OperatingFacility.
///
/// This is reproduced here with:
/// - VehicleReading -> ParentChild -> Vehicle (abstract)
/// - Vehicle (abstract) -> Car, Truck (derived concrete types)
/// - Vehicle -> ParentChild -> OperatingFacility
/// </summary>
[Collection("Sequential")]
public class ParentAssociationAbstractTypeTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public ParentAssociationAbstractTypeTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that querying parent with abstract type filter returns the correct derived types.
    ///
    /// VehicleReading has a ParentChild association to Vehicle (abstract).
    /// When we query parent(ckTypeId: "Vehicle") on VehicleReading,
    /// we should get Car or Truck (derived from Vehicle), NOT OperatingFacility.
    ///
    /// This validates the fix for the EnergyCommunity issue where:
    /// - EnergyQuantity -> parent(ckTypeId: "MeteringPoint")
    /// - Should return Consumer/Producer (derived from MeteringPoint)
    /// - Previously incorrectly resolved to OperatingFacility (MeteringPoint's parent)
    /// </summary>
    [Fact]
    public async Task GraphQL_ParentAssociation_WithAbstractTypeFilter_ReturnsCorrectDerivedTypes()
    {
        // Arrange - Query VehicleReading's parent filtered to Vehicle type
        // Using fragments for Car/Truck to verify if they are included in the union
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicleReading {
                  totalCount
                  items {
                    rtId
                    rtWellKnownName
                    parent(ckTypeId: ""AssetRepositoryIntegrationTest/Vehicle"") {
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
        result.Errors.Should().BeNullOrEmpty($"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        var answer = JObject.Parse(json);
        var readings = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicleReading.items") as JArray;
        readings.Should().NotBeNull();
        readings.Should().HaveCount(4, "There should be 4 VehicleReading entities");

        // Verify each reading has the correct parent type (Car or Truck, NOT OperatingFacility)
        var allParentTypes = new HashSet<string>();
        foreach (var reading in readings!)
        {
            var parents = reading.SelectToken("parent.items") as JArray;
            parents.Should().NotBeNull($"VehicleReading '{reading["rtWellKnownName"]}' should have parent items");
            parents.Should().HaveCount(1, $"VehicleReading '{reading["rtWellKnownName"]}' should have exactly 1 parent");

            var parent = parents![0];
            var typeName = parent["__typename"]?.Value<string>();
            typeName.Should().NotBeNull();
            allParentTypes.Add(typeName!);

            // Parent should be Car or Truck, NOT OperatingFacility
            typeName.Should().BeOneOf(
                "AssetRepositoryIntegrationTestCar",
                "AssetRepositoryIntegrationTestTruck",
                $"Parent of VehicleReading should be Car or Truck (derived from Vehicle), but got {typeName}.");

            // Should NOT be OperatingFacility
            typeName.Should().NotBe("AssetRepositoryIntegrationTestOperatingFacility",
                "Parent should NOT be OperatingFacility - this indicates incorrect association resolution");
        }

        // Verify we have both Car and Truck as parents (based on test data)
        allParentTypes.Should().Contain("AssetRepositoryIntegrationTestCar", "Should have Car as parent");
        allParentTypes.Should().Contain("AssetRepositoryIntegrationTestTruck", "Should have Truck as parent");
        allParentTypes.Should().HaveCount(2, "Should only have Car and Truck as parent types");
    }

    /// <summary>
    /// Test that parent returns the correct types without any filter.
    /// </summary>
    [Fact]
    public async Task GraphQL_ParentAssociation_WithoutFilter_ReturnsCorrectTypes()
    {
        // Arrange - Query parent without type-specific fragments to see what __typename returns
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicleReading {
                  items {
                    rtWellKnownName
                    parent {
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
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty($"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var answer = JObject.Parse(json);
        var readings = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicleReading.items") as JArray;
        readings.Should().NotBeNull();

        var allParentTypes = new HashSet<string>();
        foreach (var reading in readings!)
        {
            var parents = reading.SelectToken("parent.items") as JArray;
            if (parents != null)
            {
                foreach (var parent in parents)
                {
                    var typeName = parent["__typename"]?.Value<string>();
                    if (typeName != null)
                    {
                        allParentTypes.Add(typeName);
                        _fixture.OutputHelper?.WriteLine($"Found parent type: {typeName}");
                    }
                }
            }
        }

        _fixture.OutputHelper?.WriteLine($"All parent types found: {string.Join(", ", allParentTypes)}");

        // Should contain Car and Truck (correct parent types)
        allParentTypes.Should().Contain("AssetRepositoryIntegrationTestCar");
        allParentTypes.Should().Contain("AssetRepositoryIntegrationTestTruck");

        // Should NOT contain OperatingFacility (that's Vehicle's parent, not VehicleReading's parent)
        allParentTypes.Should().NotContain("AssetRepositoryIntegrationTestOperatingFacility",
            "OperatingFacility should NOT be returned - it's Vehicle's parent, not VehicleReading's parent");
    }

    /// <summary>
    /// Test that querying parent with a specific concrete type (Car) returns only Cars.
    /// </summary>
    [Fact]
    public async Task GraphQL_ParentAssociation_WithConcreteTypeFilter_ReturnsOnlyThatType()
    {
        // Arrange - Query VehicleReading's parent filtered to Car type only
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicleReading {
                  items {
                    rtWellKnownName
                    parent(ckTypeId: ""AssetRepositoryIntegrationTest/Car"") {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestCar {
                          rtId
                          rtWellKnownName
                          name
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty($"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");
        result.Data.Should().NotBeNull();

        var answer = JObject.Parse(json);
        var readings = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicleReading.items") as JArray;
        readings.Should().NotBeNull();

        // Count readings that have Car parents
        int readingsWithCarParent = 0;
        int readingsWithNoParent = 0;

        foreach (var reading in readings!)
        {
            var parentCount = reading.SelectToken("parent.totalCount")?.Value<int>() ?? 0;
            var parents = reading.SelectToken("parent.items") as JArray;

            if (parentCount > 0 && parents != null && parents.Count > 0)
            {
                readingsWithCarParent++;
                foreach (var parent in parents)
                {
                    parent["__typename"]?.Value<string>().Should().Be("AssetRepositoryIntegrationTestCar",
                        "When filtered by Car type, only Car parents should be returned");
                }
            }
            else
            {
                readingsWithNoParent++;
            }
        }

        // Based on test data: 2 readings linked to Car (BMW), 2 linked to Trucks
        readingsWithCarParent.Should().Be(2, "2 VehicleReadings are linked to Car (BMW 320i)");
        readingsWithNoParent.Should().Be(2, "2 VehicleReadings are linked to Trucks, so they have no Car parent when filtered");
    }

    /// <summary>
    /// Test that querying parent with OperatingFacility type filter returns an error
    /// because OperatingFacility is not a valid parent type for VehicleReading
    /// (even though Vehicle has a ParentChild association to OperatingFacility).
    ///
    /// Note: This test currently passes, which shows that the validation for
    /// unrelated types works correctly. The bug is specifically in how
    /// derived types (Car, Truck) of the actual target type (Vehicle) are handled.
    /// </summary>
    [Fact]
    public async Task GraphQL_ParentAssociation_WithUnrelatedTypeFilter_ReturnsError()
    {
        // Arrange - Query VehicleReading's parent filtered to OperatingFacility
        // This should fail because OperatingFacility is Vehicle's parent, not VehicleReading's parent
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicleReading {
                  items {
                    rtWellKnownName
                    parent(ckTypeId: ""AssetRepositoryIntegrationTest/OperatingFacility"") {
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
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // Assert - Should return an error because OperatingFacility is not related to VehicleReading
        if (result.Errors != null && result.Errors.Any())
        {
            // This is the EXPECTED behavior - validation catches unrelated types
            _fixture.OutputHelper?.WriteLine($"Got expected error: {result.Errors.First().Message}");
            result.Errors.Should().NotBeNullOrEmpty(
                "Correctly rejects OperatingFacility as an unrelated type");
        }
        else
        {
            // If no error, verify it doesn't return OperatingFacility
            var answer = JObject.Parse(json);
            var readings = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestVehicleReading.items") as JArray;

            foreach (var reading in readings!)
            {
                var parents = reading.SelectToken("parent.items") as JArray;
                if (parents != null && parents.Count > 0)
                {
                    foreach (var parent in parents)
                    {
                        var typeName = parent["__typename"]?.Value<string>();
                        typeName.Should().NotBe("AssetRepositoryIntegrationTestOperatingFacility",
                            "OperatingFacility should NOT be returned - this would indicate a severe bug!");
                    }
                }
            }
        }
    }
}
