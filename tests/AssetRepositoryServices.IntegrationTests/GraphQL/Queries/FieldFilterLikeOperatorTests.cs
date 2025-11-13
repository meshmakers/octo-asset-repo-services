using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for GraphQL fieldFilter with LIKE operator.
/// Tests various pattern matching scenarios against customer data.
/// </summary>
[Collection("Sequential")]
public class FieldFilterLikeOperatorTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public FieldFilterLikeOperatorTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_FindsSingleMatch()
    {
        // Arrange - Search for "ll" in lastName (should find "Müller")
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""lastName"", comparisonValue: ""ll"", operator:LIKE}]){
                  totalCount
                  items{
                    firstName
                    lastName
                    eMailAddress
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
        customersArray.Should().HaveCount(1, "Only 'Müller' contains 'll' in lastName");

        var customer = customersArray[0];
        customer["firstName"]?.Value<string>().Should().Be("Anna");
        customer["lastName"]?.Value<string>().Should().Be("Müller");
        customer["eMailAddress"]?.Value<string>().Should().Be("anna.mueller@example.com");

        // Verify totalCount matches
        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount?.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_FindsMultipleMatches()
    {
        // Arrange - Search for "er" in lastName (should find "Mustermann", "Müller" and "Weber")
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""lastName"", comparisonValue: ""er"", operator:LIKE}]){
                  totalCount
                  items{
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(3, "'Mustermann', 'Müller' and 'Weber' contain 'er' in lastName");

        // Verify the found customers
        var lastNames = customersArray.Select(c => c["lastName"]?.Value<string>()).ToList();
        lastNames.Should().Contain("Mustermann");
        lastNames.Should().Contain("Müller");
        lastNames.Should().Contain("Weber");

        // Verify totalCount
        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount?.Value<int>().Should().Be(3);
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_FindsNoMatches()
    {
        // Arrange - Search for "xyz" in lastName (should find nothing)
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""lastName"", comparisonValue: ""xyz"", operator:LIKE}]){
                  totalCount
                  items{
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().BeEmpty("No customer lastName contains 'xyz'");

        // Verify totalCount is 0
        var totalCount = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.totalCount");
        totalCount?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_FindsPartialMatch()
    {
        // Arrange - Search for "ust" in lastName (should find "Mustermann")
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""lastName"", comparisonValue: ""ust"", operator:LIKE}]){
                  totalCount
                  items{
                    firstName
                    lastName
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

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(1, "Only 'Mustermann' contains 'ust' in lastName");

        var customer = customersArray[0];
        customer["firstName"]?.Value<string>().Should().Be("Max");
        customer["lastName"]?.Value<string>().Should().Be("Mustermann");
        customer["rtWellKnownName"]?.Value<string>().Should().Be("CustomerMaxMustermann");
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_CaseInsensitive()
    {
        // Arrange - Search for "MIDT" in lastName (should find "Schmidt" - case insensitive)
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""lastName"", comparisonValue: ""MIDT"", operator:LIKE}]){
                  totalCount
                  items{
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(1, "LIKE operator should be case-insensitive and find 'Schmidt'");

        var customer = customersArray[0];
        customer["firstName"]?.Value<string>().Should().Be("Hans");
        customer["lastName"]?.Value<string>().Should().Be("Schmidt");
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_OnFirstName()
    {
        // Arrange - Search for "ax" in firstName (should find "Max")
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(fieldFilter:[{attributePath:""firstName"", comparisonValue: ""ax"", operator:LIKE}]){
                  totalCount
                  items{
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
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var customers = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items");
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;
        customersArray.Should().HaveCount(1, "Only 'Max' contains 'ax' in firstName");

        var customer = customersArray[0];
        customer["firstName"]?.Value<string>().Should().Be("Max");
        customer["lastName"]?.Value<string>().Should().Be("Mustermann");
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithMultipleFilters_LikeAndEquals()
    {
        // Arrange - Combine LIKE on firstName with another filter
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(
                  fieldFilter:[
                    {attributePath:""firstName"", comparisonValue: ""a"", operator:LIKE},
                    {attributePath:""city"", comparisonValue: ""Salzburg"", operator:EQUALS}
                  ]
                ){
                  totalCount
                  items{
                    firstName
                    lastName
                    city
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
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;

        // Should find "Anna" (contains 'a') and "Hans" (contains 'a') in Salzburg
        // Max also contains 'a' and is in Salzburg
        customersArray.Should().HaveCountGreaterThan(0, "Should find customers with 'a' in firstName from Salzburg");

        // Verify all returned customers have 'a' in firstName and city is Salzburg
        foreach (var customer in customersArray)
        {
            var firstName = customer["firstName"]?.Value<string>();
            firstName.Should().NotBeNullOrEmpty();
            firstName.ToLower().Should().Contain("a", "firstName should contain 'a'");

            var city = customer["city"]?.Value<string>();
            city.Should().Be("Salzburg");
        }
    }

    [Fact]
    public async Task GraphQL_QueryCustomersWithLikeFilter_OnEmailAddress()
    {
        // Arrange - Search for "example.com" in eMailAddress
        var query = @"
            query{
              runtime{
                assetRepositoryIntegrationTestCustomer(
                  fieldFilter:[{attributePath:""eMailAddress"", comparisonValue: ""example.com"", operator:LIKE}]
                ){
                  totalCount
                  items{
                    firstName
                    lastName
                    eMailAddress
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
        customers.Should().NotBeNull();
        customers.Type.Should().Be(JTokenType.Array);

        var customersArray = (JArray)customers;

        // Max, Anna, and Peter have @example.com emails (Hans has @techsolutions.at)
        customersArray.Should().HaveCount(3, "Three customers have @example.com email addresses");

        // Verify all have example.com in email
        foreach (var customer in customersArray)
        {
            var email = customer["eMailAddress"]?.Value<string>();
            email.Should().NotBeNullOrEmpty();
            email.Should().Contain("example.com");
        }
    }
}
