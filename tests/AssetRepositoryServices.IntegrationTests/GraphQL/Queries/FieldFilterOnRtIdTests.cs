using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for GraphQL fieldFilter operators on rtId field.
/// Verifies that string-based operators (LIKE, CONTAINS, etc.) work correctly
/// on rtId without requiring a valid OctoObjectId format.
/// </summary>
[Collection("Sequential")]
public class FieldFilterOnRtIdTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public FieldFilterOnRtIdTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task GraphQL_QueryWithLikeFilterOnRtId_ShouldNotThrowFormatException()
    {
        // Arrange - Search for partial rtId using LIKE operator
        // This should not throw "is not a valid 24 digit hex string" error
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""rtId"", comparisonValue: ""fd"", operator:LIKE}]){
                  totalCount
                  items{
                    rtId
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
        result.Errors.Should().BeNullOrEmpty("LIKE filter on rtId should not throw FormatException");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull("Path data.runtime.assetRepositoryIntegrationTestCustomer.items should exist");
        customers!.Type.Should().Be(JTokenType.Array);
    }

    [Fact]
    public async Task GraphQL_QueryWithMatchRegExFilterOnRtId_ShouldNotThrowFormatException()
    {
        // Arrange - Search for rtId using MATCH_REG_EX operator
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""rtId"", comparisonValue: ""^[0-9a-f]+$"", operator:MATCH_REG_EX}]){
                  totalCount
                  items{
                    rtId
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
        result.Errors.Should().BeNullOrEmpty("MATCH_REG_EX filter on rtId should not throw FormatException");
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task GraphQL_QueryWithEqualsFilterOnRtId_ShouldWorkWithValidOctoObjectId()
    {
        // Arrange - First get a valid rtId from an existing customer
        var getQuery = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(first: 1){
                  items{
                    rtId
                    firstName
                    lastName
                  }
                }
              }
            }";

        var getResult = await _fixture.ExecuteGraphQlAsync(getQuery);
        getResult.Errors.Should().BeNullOrEmpty();

        var getJson = _fixture.SerializeGraphQl(getResult);
        var getAnswer = JObject.Parse(getJson);
        var rtId = getAnswer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items[0].rtId")?.Value<string>();
        rtId.Should().NotBeNullOrEmpty();

        // Now query with EQUALS using the valid rtId
        var query = $@"
            query{{
              runtime{{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{{attributePath:""rtId"", comparisonValue: ""{rtId}"", operator:EQUALS}}]){{
                  totalCount
                  items{{
                    rtId
                    firstName
                    lastName
                  }}
                }}
              }}
            }}";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty("EQUALS filter on rtId with valid OctoObjectId should work");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount?.Value<int>().Should().Be(1, "Should find exactly one customer with matching rtId");
    }

    [Fact]
    public async Task GraphQL_QueryWithLikeFilterOnRtId_FindsMatchingEntities()
    {
        // Arrange - First get rtIds to know what pattern to search for
        var getQuery = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(first: 5){
                  items{
                    rtId
                    firstName
                  }
                }
              }
            }";

        var getResult = await _fixture.ExecuteGraphQlAsync(getQuery);
        getResult.Errors.Should().BeNullOrEmpty();

        var getJson = _fixture.SerializeGraphQl(getResult);
        var getAnswer = JObject.Parse(getJson);
        var firstRtId = getAnswer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items[0].rtId")?.Value<string>();
        firstRtId.Should().NotBeNullOrEmpty();

        // Take first 4 characters for LIKE search
        var searchPattern = firstRtId!.Substring(0, 4);

        var query = $@"
            query{{
              runtime{{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{{attributePath:""rtId"", comparisonValue: ""{searchPattern}"", operator:LIKE}}]){{
                  totalCount
                  items{{
                    rtId
                    firstName
                  }}
                }}
              }}
            }}";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        items.Should().NotBeNull();

        // All returned items should contain the search pattern in their rtId
        foreach (var item in items!)
        {
            var itemRtId = item["rtId"]?.Value<string>();
            itemRtId.Should().NotBeNullOrEmpty();
            itemRtId!.ToLower().Should().Contain(searchPattern.ToLower(),
                "LIKE filter should return entities whose rtId contains the search pattern");
        }
    }
}
