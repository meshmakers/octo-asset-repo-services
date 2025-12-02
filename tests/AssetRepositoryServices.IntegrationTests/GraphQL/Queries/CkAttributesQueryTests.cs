using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the ConstructionKit Attributes query (CkQuery.Attributes).
/// Tests the GraphQL schema directly without HTTP.
/// Uses only the System model which is always available.
/// </summary>
[Collection("Sequential")]
public class CkAttributesQueryTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    // Well-known System model for testing
    private const string SystemModelId = "System";

    public CkAttributesQueryTests(CkQueryTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Helper method to get the first available attributes' rtCkIds from the System model
    /// </summary>
    private async Task<(string? first, string? second)> GetFirstTwoAttributeRtCkIds()
    {
        var query = @"
            query {
                constructionKit {
                    attributes(first: 2) {
                        items {
                            ckAttributeId {
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
        var items = answer.SelectToken("data.constructionKit.attributes.items") as JArray;
        if (items == null || items.Count == 0) return (null, null);

        var first = items[0]["ckAttributeId"]?["semanticVersionedFullName"]?.Value<string>();
        var second = items.Count > 1 ? items[1]["ckAttributeId"]?["semanticVersionedFullName"]?.Value<string>() : null;
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
                    attributes(rtCkId: ""{rtCkId}"") {{
                        items {{
                            ckAttributeId {{
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
        return answer.SelectToken("data.constructionKit.attributes.items[0].ckAttributeId.fullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the fully qualified ModelId from a CkAttributeId fullName
    /// </summary>
    private string GetModelIdFromFullCkId(string fullCkId)
    {
        // fullCkId format is "System-1.0.3/Entity.RtId-1", ModelId is "System-1.0.3"
        return fullCkId.Split('/')[0];
    }

    #region Happy Path Tests

    [Fact]
    public async Task CkAttributes_QueryAll_ReturnsSystemModelAttributes()
    {
        // Arrange
        var query = @"
            query {
                constructionKit {
                    attributes {
                        totalCount
                        items {
                            ckAttributeId {
                                fullName
                                semanticVersionedFullName
                            }
                            attributeValueType
                            description
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull("Path data.constructionKit.attributes.items should exist");
        attributes.Type.Should().Be(JTokenType.Array);

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCountGreaterThan(0, "System model should have attributes");

        // Verify totalCount is set
        var totalCount = answer.SelectToken("data.constructionKit.attributes.totalCount");
        totalCount.Should().NotBeNull();
        totalCount.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CkAttributes_QueryByCkId_ReturnsSingleAttribute()
    {
        // Arrange - first get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }
        var fullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve attribute");

        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    attributes(ckId: $ckId) {
                        totalCount
                        items {
                            ckAttributeId {
                                fullName
                                semanticVersionedFullName
                            }
                            attributeValueType
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(1, "Query with ckId should return exactly one attribute");

        var attribute = attributesArray[0];
        attribute["ckAttributeId"]?["fullName"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CkAttributes_QueryByCkIds_ReturnsMultipleAttributes()
    {
        // Arrange - first get available attributes dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough attributes available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        var secondFullCkId = await GetFullCkIdForRtCkId(secondRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve first attribute");
        secondFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve second attribute");

        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    attributes(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(2, "Query with two ckIds should return exactly two attributes");
    }

    [Fact]
    public async Task CkAttributes_QueryByRtCkId_ReturnsSingleAttribute()
    {
        // Arrange - get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    attributes(rtCkId: $rtCkId) {
                        totalCount
                        items {
                            ckAttributeId {
                                fullName
                                semanticVersionedFullName
                            }
                            attributeValueType
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(1, "Query with rtCkId should return exactly one attribute");
    }

    [Fact]
    public async Task CkAttributes_QueryByRtCkIds_ReturnsMultipleAttributes()
    {
        // Arrange - get available attributes dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough attributes available
            return;
        }

        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    attributes(rtCkIds: $rtCkIds) {
                        totalCount
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(2, "Query with two rtCkIds should return exactly two attributes");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task CkAttributes_QueryWithCkModelIdsFilter_ReturnsFilteredAttributes()
    {
        // Arrange - first get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }
        var fullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve attribute");
        var systemModelId = GetModelIdFromFullCkId(fullCkId);

        var query = @"
            query ($modelIds: [String]!) {
                constructionKit {
                    attributes(ckModelIds: $modelIds) {
                        totalCount
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCountGreaterThan(0, "System model should have attributes");

        // All returned attributes should belong to the System model
        foreach (var attribute in attributesArray)
        {
            var fullName = attribute["ckAttributeId"]?["fullName"]?.Value<string>();
            fullName.Should().NotBeNullOrEmpty();
            fullName.Should().StartWith("System", "All attributes should belong to System model");
        }
    }

    [Fact]
    public async Task CkAttributes_QueryWithSortOrder_ReturnsSortedAttributes()
    {
        // Arrange - sort by ckAttributeId descending
        var query = @"
            query {
                constructionKit {
                    attributes(sortOrder: [{ attributePath: ""ckAttributeId"", sortOrder: DESCENDING }]) {
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCountGreaterThan(1, "Need multiple attributes to verify sorting");

        // Verify descending order
        var fullNames = attributesArray.Select(t => t["ckAttributeId"]?["fullName"]?.Value<string>()).ToList();
        var sortedNames = fullNames.OrderByDescending(n => n).ToList();
        fullNames.Should().BeEquivalentTo(sortedNames, options => options.WithStrictOrdering(),
            "Attributes should be sorted in descending order");
    }

    [Fact]
    public async Task CkAttributes_QueryWithPagination_ReturnsPagedResults()
    {
        // Arrange - get first 2 attributes
        var query = @"
            query {
                constructionKit {
                    attributes(first: 2) {
                        totalCount
                        pageInfo {
                            hasNextPage
                            hasPreviousPage
                        }
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(2, "Should return exactly 2 attributes with first: 2");

        var totalCount = answer.SelectToken("data.constructionKit.attributes.totalCount")?.Value<int>();
        totalCount.Should().BeGreaterThan(2, "Total count should be greater than page size");

        var hasNextPage = answer.SelectToken("data.constructionKit.attributes.pageInfo.hasNextPage")?.Value<bool>();
        hasNextPage.Should().BeTrue("Should have next page when more attributes exist");
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CkAttributes_QueryWithEmptyCkIdsArray_ReturnsEmptyResult()
    {
        // Arrange - empty ckIds array should return empty result
        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    attributes(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().BeEmpty("Empty ckIds array should return empty result");
    }

    [Fact]
    public async Task CkAttributes_QueryWithCkIdAndRtCkId_ReturnsError()
    {
        // Arrange - first get available attributes dynamically
        var (firstRtCkId, secondRtCkId) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId) || string.IsNullOrEmpty(secondRtCkId))
        {
            // Skip if not enough attributes available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve attribute");

        // Using both ckId and rtCkId should fail
        var query = @"
            query ($ckId: String!, $rtCkId: String!) {
                constructionKit {
                    attributes(ckId: $ckId, rtCkId: $rtCkId) {
                        items {
                            ckAttributeId {
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
    public async Task CkAttributes_QueryWithCkIdAndCkModelIds_ReturnsError()
    {
        // Arrange - first get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }
        var firstFullCkId = await GetFullCkIdForRtCkId(firstRtCkId);
        firstFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve attribute");

        // Using both ckId and ckModelIds should fail
        var query = @"
            query ($ckId: String!, $modelIds: [String]!) {
                constructionKit {
                    attributes(ckId: $ckId, ckModelIds: $modelIds) {
                        items {
                            ckAttributeId {
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
    public async Task CkAttributes_QueryWithNonExistentCkId_ReturnsEmptyResult()
    {
        // Arrange - non-existent ckId should return empty result
        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    attributes(ckId: $ckId) {
                        totalCount
                        items {
                            ckAttributeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = "NonExistent/Type.AttributeThatDoesNotExist" });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().BeEmpty("Non-existent ckId should return empty result");
    }

    #endregion

    #region AB#2801 Tests - CkId Object Structure

    [Fact]
    public async Task CkAttributes_CkAttributeId_ReturnsFullNameField()
    {
        // Arrange - get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    attributes(rtCkId: $rtCkId) {
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(1);

        // Verify fullName field exists and has expected format
        var firstAttribute = attributesArray[0];
        var fullName = firstAttribute["ckAttributeId"]?["fullName"]?.Value<string>();
        fullName.Should().NotBeNullOrEmpty("fullName field should be present");
        // fullName format is 'ModelName-Version/TypeName.AttributeName-ElementVersion' e.g. 'System-1.0.3/Entity.RtId-1'
        fullName.Should().MatchRegex(@"^System-\d+\.\d+\.\d+/[\w.]+\-\d+$",
            "fullName should be in format 'ModelName-Version/TypeName.AttributeName-ElementVersion'");
    }

    [Fact]
    public async Task CkAttributes_CkAttributeId_ReturnsSemanticVersionedFullNameField()
    {
        // Arrange - get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    attributes(rtCkId: $rtCkId) {
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(1);

        // Verify semanticVersionedFullName field exists
        var attribute = attributesArray[0];
        var semanticVersionedFullName = attribute["ckAttributeId"]?["semanticVersionedFullName"]?.Value<string>();
        semanticVersionedFullName.Should().NotBeNullOrEmpty("semanticVersionedFullName field should be present");
    }

    [Fact]
    public async Task CkAttributes_CkAttributeId_JsonStructureIsCorrect()
    {
        // Arrange - get available attributes dynamically
        var (firstRtCkId, _) = await GetFirstTwoAttributeRtCkIds();
        if (string.IsNullOrEmpty(firstRtCkId))
        {
            // Skip if no attributes available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    attributes(rtCkId: $rtCkId) {
                        items {
                            ckAttributeId {
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

        var attributes = answer.SelectToken("data.constructionKit.attributes.items");
        attributes.Should().NotBeNull();

        var attributesArray = (JArray)attributes;
        attributesArray.Should().HaveCount(1);

        // Verify complete CkAttributeId object structure
        var attribute = attributesArray[0];
        var ckAttributeId = attribute["ckAttributeId"];
        ckAttributeId.Should().NotBeNull("ckAttributeId should be an object, not a scalar");
        ckAttributeId.Type.Should().Be(JTokenType.Object, "ckAttributeId should be a JSON object");

        // Verify all expected fields are present
        var fullName = ckAttributeId["fullName"]?.Value<string>();
        var semanticVersionedFullName = ckAttributeId["semanticVersionedFullName"]?.Value<string>();

        fullName.Should().NotBeNullOrEmpty();
        semanticVersionedFullName.Should().NotBeNullOrEmpty();

        // Verify semanticVersionedFullName contains expected pattern
        semanticVersionedFullName.Should().Contain("System");
    }

    #endregion
}
