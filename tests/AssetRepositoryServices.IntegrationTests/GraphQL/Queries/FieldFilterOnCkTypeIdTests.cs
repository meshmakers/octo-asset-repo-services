using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;
using ITestOutputHelper = Xunit.ITestOutputHelper;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for GraphQL fieldFilter operators on ckTypeId field.
/// Verifies that EQUALS operator works correctly on ckTypeId to filter entities by type.
/// </summary>
[Collection("Sequential")]
public class FieldFilterOnCkTypeIdTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public FieldFilterOnCkTypeIdTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task GraphQL_QueryWithEqualsFilterOnCkTypeId_ShouldReturnMatchingEntities()
    {
        // Arrange - Query customers with EQUALS filter on ckTypeId
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""ckTypeId"", comparisonValue: ""AssetRepositoryIntegrationTest/Customer"", operator:EQUALS}]){
                  totalCount
                  items{
                    rtId
                    ckTypeId
                    firstName
                    lastName
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty("EQUALS filter on ckTypeId should not throw errors");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount.Should().NotBeNull("totalCount should be present");
        totalCount!.Value<int>().Should().BeGreaterThan(0,
            "EQUALS filter on ckTypeId should return customers (not 0 results)");

        var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        items.Should().NotBeNull("items should be present");
        items!.Count.Should().BeGreaterThan(0, "Should return at least one customer");

        // Verify all returned items have the correct ckTypeId
        foreach (var item in items)
        {
            var ckTypeId = item["ckTypeId"]?.Value<string>();
            ckTypeId.Should().Be("AssetRepositoryIntegrationTest/Customer",
                "All returned entities should have the filtered ckTypeId");
        }
    }

    [Fact]
    public async Task GraphQL_QueryWithEqualsFilterOnCkTypeId_ShouldMatchUnfilteredResults()
    {
        // Arrange - First query all customers without filter
        var unfilteredQuery = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer{
                  totalCount
                  items{
                    rtId
                    ckTypeId
                    firstName
                    lastName
                  }
                }
              }
            }";

        var unfilteredResult = await _fixture.ExecuteGraphQlAsync(unfilteredQuery);
        unfilteredResult.Errors.Should().BeNullOrEmpty();

        var unfilteredJson = _fixture.SerializeGraphQl(unfilteredResult);
        var unfilteredAnswer = JObject.Parse(unfilteredJson);
        var unfilteredCount = unfilteredAnswer
            .SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount")?.Value<int>() ?? 0;

        unfilteredCount.Should().BeGreaterThan(0, "Test data should contain customers");

        // Now query with EQUALS filter on ckTypeId
        var filteredQuery = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""ckTypeId"", comparisonValue: ""AssetRepositoryIntegrationTest/Customer"", operator:EQUALS}]){
                  totalCount
                  items{
                    rtId
                    ckTypeId
                    firstName
                    lastName
                  }
                }
              }
            }";

        // Act
        var filteredResult = await _fixture.ExecuteGraphQlAsync(filteredQuery);

        // Assert
        filteredResult.Should().NotBeNull();
        filteredResult.Errors.Should().BeNullOrEmpty("EQUALS filter on ckTypeId should not throw errors");

        var filteredJson = _fixture.SerializeGraphQl(filteredResult);
        var filteredAnswer = JObject.Parse(filteredJson);

        var filteredCount = filteredAnswer
            .SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount")?.Value<int>() ?? 0;

        // Since we're querying the Customer type endpoint with a filter for Customer ckTypeId,
        // the results should be the same (all customers have ckTypeId = AssetRepositoryIntegrationTest/Customer)
        filteredCount.Should().Be(unfilteredCount,
            "Filtering on ckTypeId=Customer when querying Customer type should return same count as unfiltered query");
    }

    [Fact]
    public async Task GraphQL_QueryVehiclesWithEqualsFilterOnCkTypeId_ShouldFilterByDerivedType()
    {
        // Arrange - Query only Cars (not Trucks) using ckTypeId filter
        // This tests filtering within a type hierarchy
        var carQuery = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCar(fieldFilter:[{attributePath:""ckTypeId"", comparisonValue: ""AssetRepositoryIntegrationTest/Car"", operator:EQUALS}]){
                  totalCount
                  items{
                    rtId
                    ckTypeId
                    licensePlate
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(carQuery);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty("EQUALS filter on ckTypeId should not throw errors");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.totalCount");
        totalCount.Should().NotBeNull("totalCount should be present");
        totalCount!.Value<int>().Should().BeGreaterThan(0,
            "EQUALS filter on ckTypeId should return cars (test data has 2 cars)");

        var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCar.items") as JArray;
        items.Should().NotBeNull("items should be present");

        // Verify all returned items have the correct ckTypeId
        foreach (var item in items!)
        {
            var ckTypeId = item["ckTypeId"]?.Value<string>();
            ckTypeId.Should().Be("AssetRepositoryIntegrationTest/Car",
                "All returned entities should have the Car ckTypeId");
        }
    }

    [Fact]
    public async Task GraphQL_QueryWithLikeFilterOnCkTypeId_ShouldReturnMatchingEntities()
    {
        // Arrange - Query customers with LIKE filter on ckTypeId (partial match)
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""ckTypeId"", comparisonValue: ""Customer"", operator:LIKE}]){
                  totalCount
                  items{
                    rtId
                    ckTypeId
                    firstName
                    lastName
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty("LIKE filter on ckTypeId should not throw errors");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount.Should().NotBeNull("totalCount should be present");
        totalCount!.Value<int>().Should().BeGreaterThan(0,
            "LIKE filter on ckTypeId should return customers");

        var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        items.Should().NotBeNull("items should be present");

        // Verify all returned items have ckTypeId containing "Customer"
        foreach (var item in items!)
        {
            var ckTypeId = item["ckTypeId"]?.Value<string>();
            ckTypeId.Should().Contain("Customer",
                "All returned entities should have ckTypeId containing 'Customer'");
        }
    }

    [Fact]
    public async Task GraphQL_QueryWithEqualsFilterOnCkTypeId_UsingWrongValue_ShouldReturnNoResults()
    {
        // Arrange - Query customers with EQUALS filter on non-existent ckTypeId
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""ckTypeId"", comparisonValue: ""NonExistent/Type"", operator:EQUALS}]){
                  totalCount
                  items{
                    rtId
                    ckTypeId
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty("EQUALS filter on ckTypeId with non-matching value should not throw errors");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount.Should().NotBeNull("totalCount should be present");
        totalCount!.Value<int>().Should().Be(0,
            "EQUALS filter with non-matching ckTypeId should return 0 results");
    }
}
