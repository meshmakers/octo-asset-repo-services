using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the ConstructionKit Enums query (CkQuery.Enums).
/// Tests the GraphQL schema directly without HTTP.
/// Uses only the System model which is always available.
/// </summary>
[Collection("Sequential")]
public class CkEnumsQueryTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    // Well-known System model for testing
    private const string SystemModelId = "System";

    public CkEnumsQueryTests(CkQueryTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Helper method to get the first available enums' rtCkIds from the System model
    /// </summary>
    private async Task<(string? first, string? second)> GetFirstTwoEnumRtCkIds()
    {
        var query = @"
            query {
                constructionKit {
                    enums(first: 2) {
                        items {
                            ckEnumId {
                                semanticVersionedFullName
                                fullName
                            }
                        }
                    }
                }
            }";

        var result = await _fixture.ExecuteGraphQlAsync(query);
        if (result.Errors != null && result.Errors.Any()) return (null, null);

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);
        var items = answer.SelectToken("data.constructionKit.enums.items") as JArray;
        if (items == null || items.Count == 0) return (null, null);

        var first = items[0]["ckEnumId"]?["semanticVersionedFullName"]?.Value<string>();
        var second = items.Count > 1 ? items[1]["ckEnumId"]?["semanticVersionedFullName"]?.Value<string>() : null;
        return (first, second);
    }

    /// <summary>
    /// Helper method to get the fully qualified ckId (with version) for a given rtCkId
    /// </summary>
    private async Task<string?> GetFullCkIdForRtCkId(string rtCkId)
    {
        var query = $@"
            query {{
                constructionKit {{
                    enums(rtCkId: ""{rtCkId}"") {{
                        items {{
                            ckEnumId {{
                                fullName
                            }}
                        }}
                    }}
                }}
            }}";

        var result = await _fixture.ExecuteGraphQlAsync(query);
        if (result.Errors != null && result.Errors.Any()) return null;

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);
        return answer.SelectToken("data.constructionKit.enums.items[0].ckEnumId.fullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the fully qualified ModelId from a CkEnumId fullName
    /// </summary>
    private string GetModelIdFromFullCkId(string fullCkId)
    {
        // fullCkId format is "System-1.0.3/ModelState-1", ModelId is "System-1.0.3"
        return fullCkId.Split('/')[0];
    }

    #region Happy Path Tests

    [Fact]
    public async Task CkEnums_QueryAll_ReturnsSystemModelEnums()
    {
        // Arrange
        var query = @"
            query {
                constructionKit {
                    enums {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                            description
                            useFlags
                            isExtensible
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

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull("Path data.constructionKit.enums.items should exist");
        enums.Type.Should().Be(JTokenType.Array);

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCountGreaterThan(0, "System model should have enums");

        // Verify totalCount is set
        var totalCount = answer.SelectToken("data.constructionKit.enums.totalCount");
        totalCount.Should().NotBeNull();
        totalCount.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CkEnums_QueryByCkId_ReturnsSingleEnum()
    {
        // Arrange - first get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }
        var fullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve enum");

        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    enums(ckId: $ckId) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                            useFlags
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = fullCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1, "Query with ckId should return exactly one enum");
    }

    [Fact]
    public async Task CkEnums_QueryByCkIds_ReturnsMultipleEnums()
    {
        // Arrange - first get available enums dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough enums available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        var secondFullCkId = await GetFullCkIdForRtCkId(secondRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve first enum");
        secondFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve second enum");

        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    enums(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckIds = new[] { firstFullCkId, secondFullCkId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(2, "Query with two ckIds should return exactly two enums");
    }

    [Fact]
    public async Task CkEnums_QueryByRtCkId_ReturnsSingleEnum()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                            useFlags
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1, "Query with rtCkId should return exactly one enum");
    }

    [Fact]
    public async Task CkEnums_QueryByRtCkIds_ReturnsMultipleEnums()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough enums available
            return;
        }

        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    enums(rtCkIds: $rtCkIds) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkIds = new[] { firstRtCkId, secondRtCkId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(2, "Query with two rtCkIds should return exactly two enums");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task CkEnums_QueryWithCkModelIdsFilter_ReturnsFilteredEnums()
    {
        // Arrange - first get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }
        var fullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve enum");
        var systemModelId = GetModelIdFromFullCkId(fullCkId);

        var query = @"
            query ($modelIds: [String]!) {
                constructionKit {
                    enums(ckModelIds: $modelIds) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { modelIds = new[] { systemModelId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCountGreaterThan(0, "System model should have enums");

        // All returned enums should belong to the System model
        foreach (var enumItem in enumsArray)
        {
            var enumFullName = enumItem["ckEnumId"]?["fullName"]?.Value<string>();
            enumFullName.Should().NotBeNullOrEmpty();
            enumFullName.Should().StartWith("System", "All enums should belong to System model");
        }
    }

    [Fact]
    public async Task CkEnums_QueryWithSortOrder_ReturnsSortedEnums()
    {
        // Arrange - sort by ckEnumId descending
        var query = @"
            query {
                constructionKit {
                    enums(sortOrder: [{ attributePath: ""ckEnumId"", sortOrder: DESCENDING }]) {
                        items {
                            ckEnumId {
                                fullName
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

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCountGreaterThan(1, "Need multiple enums to verify sorting");

        // Verify descending order
        var fullNames = enumsArray.Select(t => t["ckEnumId"]?["fullName"]?.Value<string>()).ToList();
        var sortedNames = fullNames.OrderByDescending(n => n).ToList();
        fullNames.Should().BeEquivalentTo(sortedNames, options => options.WithStrictOrdering(),
            "Enums should be sorted in descending order");
    }

    [Fact]
    public async Task CkEnums_QueryWithPagination_ReturnsPagedResults()
    {
        // Arrange - get first 2 enums
        var query = @"
            query {
                constructionKit {
                    enums(first: 2) {
                        totalCount
                        pageInfo {
                            hasNextPage
                            hasPreviousPage
                        }
                        items {
                            ckEnumId {
                                fullName
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

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Count.Should().BeLessThanOrEqualTo(2, "Should return at most 2 enums with first: 2");

        var totalCount = answer.SelectToken("data.constructionKit.enums.totalCount")?.Value<int>();
        totalCount.Should().NotBeNull();
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CkEnums_QueryWithEmptyCkIdsArray_ReturnsEmptyResult()
    {
        // Arrange - empty ckIds array should return empty result
        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    enums(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckIds = Array.Empty<string>() });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().BeEmpty("Empty ckIds array should return empty result");
    }

    [Fact]
    public async Task CkEnums_QueryWithCkIdAndRtCkId_ReturnsError()
    {
        // Arrange - first get available enums dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough enums available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve enum");

        // Using both ckId and rtCkId should fail
        var query = @"
            query ($ckId: String!, $rtCkId: String!) {
                constructionKit {
                    enums(ckId: $ckId, rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = firstFullCkId, rtCkId = secondRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and rtCkId should produce an error");
    }

    [Fact]
    public async Task CkEnums_QueryWithCkIdAndCkModelIds_ReturnsError()
    {
        // Arrange - first get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve enum");

        // Using both ckId and ckModelIds should fail
        var query = @"
            query ($ckId: String!, $modelIds: [String]!) {
                constructionKit {
                    enums(ckId: $ckId, ckModelIds: $modelIds) {
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            ckId = firstFullCkId,
            modelIds = new[] { SystemModelId }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and ckModelIds should produce an error");
    }

    [Fact]
    public async Task CkEnums_QueryWithNonExistentCkId_ReturnsEmptyResult()
    {
        // Arrange - non-existent ckId should return empty result
        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    enums(ckId: $ckId) {
                        totalCount
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = "NonExistent/EnumThatDoesNotExist" });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().BeEmpty("Non-existent ckId should return empty result");
    }

    #endregion

    #region AB#2801 Tests - CkId Object Structure

    [Fact]
    public async Task CkEnums_CkEnumId_ReturnsFullNameField()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1);

        // Verify fullName field exists and has expected format
        var firstEnum = enumsArray[0];
        var fullName = firstEnum["ckEnumId"]?["fullName"]?.Value<string>();
        fullName.Should().NotBeNullOrEmpty("fullName field should be present");
        // fullName format is 'ModelName-Version/EnumName-ElementVersion' e.g. 'System-1.0.3/ModelState-1'
        fullName.Should().MatchRegex(@"^System-\d+\.\d+\.\d+/\w+-\d+$",
            "fullName should be in format 'ModelName-Version/EnumName-ElementVersion'");
    }

    [Fact]
    public async Task CkEnums_CkEnumId_ReturnsSemanticVersionedFullNameField()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1);

        // Verify semanticVersionedFullName field exists
        var enumItem = enumsArray[0];
        var semanticVersionedFullName = enumItem["ckEnumId"]?["semanticVersionedFullName"]?.Value<string>();
        semanticVersionedFullName.Should().NotBeNullOrEmpty("semanticVersionedFullName field should be present");
    }

    [Fact]
    public async Task CkEnums_CkEnumId_JsonStructureIsCorrect()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1);

        // Verify complete CkEnumId object structure
        var enumItem = enumsArray[0];
        var ckEnumId = enumItem["ckEnumId"];
        ckEnumId.Should().NotBeNull("ckEnumId should be an object, not a scalar");
        ckEnumId.Type.Should().Be(JTokenType.Object, "ckEnumId should be a JSON object");

        // Verify all expected fields are present
        var fullName = ckEnumId["fullName"]?.Value<string>();
        var semanticVersionedFullName = ckEnumId["semanticVersionedFullName"]?.Value<string>();

        fullName.Should().NotBeNullOrEmpty();
        semanticVersionedFullName.Should().NotBeNullOrEmpty();

        // Verify semanticVersionedFullName contains expected pattern
        semanticVersionedFullName.Should().Contain("System");
    }

    #endregion

    #region Enum-Specific Tests

    [Fact]
    public async Task CkEnums_QueryReturnsEnumValues()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                fullName
                            }
                            values {
                                name
                                key
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1);

        var enumItem = enumsArray[0];
        var values = enumItem["values"];
        values.Should().NotBeNull("values should be present");
        values.Type.Should().Be(JTokenType.Array);

        var valuesArray = (JArray)values;
        valuesArray.Should().HaveCountGreaterThan(0, "Enum should have values");

        // Check that each value has name and key fields
        foreach (var value in valuesArray)
        {
            value["name"]?.Value<string>().Should().NotBeNullOrEmpty("value should have a name");
            value["key"].Should().NotBeNull("value should have a numeric key");
        }
    }

    [Fact]
    public async Task CkEnums_QueryReturnsUseFlagsAndIsExtensible()
    {
        // Arrange - get available enums dynamically
        var (firstRtCkId, _) = await GetFirstTwoEnumRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no enums available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    enums(rtCkId: $rtCkId) {
                        items {
                            ckEnumId {
                                fullName
                            }
                            useFlags
                            isExtensible
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var enums = answer.SelectToken("data.constructionKit.enums.items");
        enums.Should().NotBeNull();

        var enumsArray = (JArray)enums;
        enumsArray.Should().HaveCount(1);

        var enumItem = enumsArray[0];
        enumItem["useFlags"].Should().NotBeNull("useFlags should be present");
        enumItem["isExtensible"].Should().NotBeNull("isExtensible should be present");
    }

    #endregion
}
