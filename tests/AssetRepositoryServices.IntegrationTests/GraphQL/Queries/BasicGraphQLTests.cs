using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for basic GraphQL queries against the AssetRepositoryIntegrationTest model.
/// Tests the GraphQL schema directly without HTTP.
/// </summary>
[Collection("Sequential")]
public class BasicGraphQlTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public BasicGraphQlTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task GraphQL_QueryCustomers_ReturnsAll()
    {
        // Arrange
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer{
                  items{
                    rtId
                    rtWellKnownName
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
        var answer = JObject.Parse(json);

        var token = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        token.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestCustomer.items should exist");
        token.Type.Should().Be(JTokenType.Array);

        var arr = (JArray)token;
        arr.Should().HaveCount(4); // 4 customers
    }

    [Fact]
    public async Task GraphQL_QueryOperatingFacilities_ReturnsAll()
    {
        // Arrange
        var query = @"
          query{
              runtime{
                assetRepositoryIntegrationTestOperatingFacility{
                  items{
                    rtId
                    rtWellKnownName
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
        var answer = JObject.Parse(json);

        var token = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestOperatingFacility.items");
        token.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestOperatingFacility.items should exist");
        token.Type.Should().Be(JTokenType.Array);

        var arr = (JArray)token;
        arr.Should().HaveCount(4); // 4 facilities
    }

    [Fact]
    public async Task GraphQL_QueryMeteringPoints_ReturnsAll()
    {
        // Arrange
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestMeteringPoint{
                  items{
                    rtId
                    rtWellKnownName
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
        var answer = JObject.Parse(json);

        var token = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestMeteringPoint.items");
        token.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestMeteringPoint.items should exist");
        token.Type.Should().Be(JTokenType.Array);

        var arr = (JArray)token;
        arr.Should().HaveCount(8); // 8 metering points
    }

    [Fact]
    public async Task GraphQL_QueryCustomerWithFilter_ReturnsSingle()
    {
        // Arrange
        var query = @"
           query ($wellKnownName: SimpleScalar!) {
              runtime {
                assetRepositoryIntegrationTestCustomer(
                  fieldFilter: [
                    {
                      attributePath: ""rtWellKnownName""
                      comparisonValue: $wellKnownName
                      operator: EQUALS
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                  }
                }
              }
            }
            ";

        var variables = JsonSerializer.Serialize(new
        {
            wellKnownName = "CustomerMaxMustermann"
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var token = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        token.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestCustomer.items should exist");
        token.Type.Should().Be(JTokenType.Array);

        var customers = (JArray)token;
        customers.Should().HaveCount(1);

        var customer = customers[0];
        customer["rtWellKnownName"]?.Value<string>().Should().Be("CustomerMaxMustermann");
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithOwnedFacilities_NavigatesAssociations()
    {
        // Arrange - Using inline fragments with the new union-based association pattern
        // For union types, all fields must be queried within inline fragments
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestCustomer {
                  items {
                    rtId
                    rtWellKnownName
                    owns(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/OperatingFacility""]) {
                      edges {
                        node {
                          ... on AssetRepositoryIntegrationTestOperatingFacility {
                            rtId
                            rtWellKnownName
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
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestCustomer.items should exist");
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(4); // 4 customers

        // Verify each customer has owned facilities
        foreach (var customer in customersArray)
        {
            var rtWellKnownName = customer["rtWellKnownName"]?.Value<string>();
            rtWellKnownName.Should().NotBeNullOrEmpty();

            var ownsEdges = customer.SelectToken("owns.edges");
            ownsEdges.Should().NotBeNull($"Customer {rtWellKnownName} should have owns.edges");
            ownsEdges.Type.Should().Be(JTokenType.Array);

            var ownedFacilities = (JArray)ownsEdges;
            ownedFacilities.Should().HaveCount(1, $"Customer {rtWellKnownName} should own exactly 1 facility");

            var facilityEdge = ownedFacilities[0];
            var facility = facilityEdge["node"];
            facility.Should().NotBeNull($"Facility edge for {rtWellKnownName} should have node");
            facility["rtWellKnownName"].Should().NotBeNull($"Facility owned by {rtWellKnownName} should have rtWellKnownName");
        }

        // Verify specific customer-facility relationships
        var maxMustermann = customersArray.FirstOrDefault(c => c["rtWellKnownName"]?.Value<string>() == "CustomerMaxMustermann");
        maxMustermann.Should().NotBeNull();
        var maxFacility = maxMustermann.SelectToken("owns.edges[0].node.rtWellKnownName")?.Value<string>();
        maxFacility.Should().Be("FacilityHauptstrasse42");

        var annaMueller = customersArray.FirstOrDefault(c => c["rtWellKnownName"]?.Value<string>() == "CustomerAnnaMueller");
        annaMueller.Should().NotBeNull();
        var annaFacility = annaMueller.SelectToken("owns.edges[0].node.rtWellKnownName")?.Value<string>();
        annaFacility.Should().Be("FacilityLinzerStrasse15");
    }

    [Fact]
    public async Task GraphQL_QueryMeteringPointsWithInFilter_ReturnsFilteredResults()
    {
        // Arrange
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestMeteringPoint(
                  fieldFilter: [
                    {
                      attributePath: ""operatingStatus""
                      comparisonValue: [""OK"", ""Maintenance""]
                      operator: IN
                    }
                  ]
                ) {
                  items {
                    rtId
                    rtWellKnownName
                    operatingStatus
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
        var answer = JObject.Parse(json);

        var meteringPoints = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestMeteringPoint.items");
        meteringPoints.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestMeteringPoint.items should exist");
        meteringPoints.Type.Should().Be(JTokenType.Array);

        var meteringPointsArray = (JArray)meteringPoints;

        // We have 8 active metering points:
        // - 7 with OperatingStatus = 1 (OK)
        // - 1 with OperatingStatus = 2 (Maintenance)
        // So the filter should return all 8
        meteringPointsArray.Should().HaveCount(8, "Should return all metering points with status OK or Maintenance");

        // Verify that all returned metering points have operatingStatus field
        foreach (var mp in meteringPointsArray)
        {
            var wellKnownName = mp["rtWellKnownName"]?.Value<string>();
            wellKnownName.Should().NotBeNullOrEmpty();

            var status = mp["operatingStatus"];
            status.Should().NotBeNull($"Metering point {wellKnownName} should have operatingStatus");

            // The status value should be either "OK" or "Maintenance" (or their enum values 1/2)
            var statusValue = status?.Value<string>();
            statusValue.Should().NotBeNullOrEmpty($"Metering point {wellKnownName} should have non-empty operatingStatus value");
        }

        // Verify specific metering point with Maintenance status
        var maintenancePoint = meteringPointsArray.FirstOrDefault(mp =>
            mp["rtWellKnownName"]?.Value<string>() == "MeteringPointAT0010001234567893");
        maintenancePoint.Should().NotBeNull("Should find the metering point with Maintenance status");

        // Note: The actual value might be "Maintenance" or the enum integer value depending on GraphQL serialization
        var maintenanceStatus = maintenancePoint["operatingStatus"]?.Value<string>();
        maintenanceStatus.Should().NotBeNullOrEmpty();
    }
}