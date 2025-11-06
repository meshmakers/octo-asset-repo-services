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
}