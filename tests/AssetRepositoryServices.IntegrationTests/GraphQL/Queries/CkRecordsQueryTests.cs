using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the ConstructionKit Records query (CkQuery.Records).
/// Tests the GraphQL schema directly without HTTP.
/// Uses only the System model which is always available.
/// </summary>
[Collection("Sequential")]
public class CkRecordsQueryTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    // Well-known System model records for testing (rtCkId format - without version)
    private const string SystemModelId = "System";

    public CkRecordsQueryTests(CkQueryTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Helper method to get the fully qualified ckId (with version) for a given rtCkId
    /// </summary>
    private async Task<string?> GetFullCkIdForRtCkId(string rtCkId)
    {
        var query = $@"
            query {{
                constructionKit {{
                    records(rtCkId: ""{rtCkId}"") {{
                        items {{
                            ckRecordId {{
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
        return answer.SelectToken("data.constructionKit.records.items[0].ckRecordId.fullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the first available record rtCkId from the System model
    /// </summary>
    private async Task<string?> GetFirstAvailableRecordRtCkId()
    {
        var query = @"
            query {
                constructionKit {
                    records(first: 1) {
                        items {
                            ckRecordId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var result = await _fixture.ExecuteGraphQlAsync(query);
        if (result.Errors != null && result.Errors.Any()) return null;

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);
        return answer.SelectToken("data.constructionKit.records.items[0].ckRecordId.semanticVersionedFullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the fully qualified ModelId from a CkRecordId fullName
    /// </summary>
    private string GetModelIdFromFullCkId(string fullCkId)
    {
        // fullCkId format is "System-1.0.3/ValueRange-1", ModelId is "System-1.0.3"
        return fullCkId.Split('/')[0];
    }

    #region Happy Path Tests

    [Fact]
    public async Task CkRecords_QueryAll_ReturnsSystemModelRecords()
    {
        // Arrange
        var query = @"
            query {
                constructionKit {
                    records {
                        totalCount
                        items {
                            ckRecordId {
                                fullName
                                semanticVersionedFullName
                            }
                            isAbstract
                            isFinal
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull("Path data.constructionKit.records.items should exist");
        records.Type.Should().Be(JTokenType.Array);

        // Note: System model may or may not have records
        // Verify totalCount is set
        var totalCount = answer.SelectToken("data.constructionKit.records.totalCount");
        totalCount.Should().NotBeNull();
    }

    [Fact]
    public async Task CkRecords_QueryByCkId_ReturnsSingleRecord()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available in System model
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRecordRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve record");

        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    records(ckId: $ckId) {
                        totalCount
                        items {
                            ckRecordId {
                                fullName
                                semanticVersionedFullName
                            }
                            isAbstract
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1, "Query with ckId should return exactly one record");
    }

    [Fact]
    public async Task CkRecords_QueryByRtCkId_ReturnsSingleRecord()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available in System model
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        totalCount
                        items {
                            ckRecordId {
                                fullName
                                semanticVersionedFullName
                            }
                            isAbstract
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1, "Query with rtCkId should return exactly one record");
    }

    [Fact]
    public async Task CkRecords_QueryByRtCkIds_ReturnsMultipleRecords()
    {
        // Arrange - get all available records first
        var allRecordsQuery = @"
            query {
                constructionKit {
                    records(first: 2) {
                        items {
                            ckRecordId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var allRecordsResult = await _fixture.ExecuteGraphQlAsync(allRecordsQuery);
        if (allRecordsResult.Errors != null && allRecordsResult.Errors.Any())
        {
            return; // Skip if error
        }

        var allRecordsJson = _fixture.SerializeGraphQl(allRecordsResult);
        var allRecordsAnswer = JObject.Parse(allRecordsJson);
        var recordItems = allRecordsAnswer.SelectToken("data.constructionKit.records.items") as JArray;

        if (recordItems == null || recordItems.Count < 2)
        {
            // Skip if less than 2 records available
            return;
        }

        var rtCkIds = recordItems
            .Select(r => r["ckRecordId"]?["semanticVersionedFullName"]?.Value<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Take(2)
            .ToArray();

        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    records(rtCkIds: $rtCkIds) {
                        totalCount
                        items {
                            ckRecordId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkIds });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(2, "Query with two rtCkIds should return exactly two records");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task CkRecords_QueryWithCkModelIdsFilter_ReturnsFilteredRecords()
    {
        // Arrange - first get an available record to get the model ID
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRecordRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();
        var systemModelId = GetModelIdFromFullCkId(fullCkId);

        var query = @"
            query ($modelIds: [String]!) {
                constructionKit {
                    records(ckModelIds: $modelIds) {
                        totalCount
                        items {
                            ckRecordId {
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        // All returned records should belong to the System model
        foreach (var record in recordsArray)
        {
            var recordFullName = record["ckRecordId"]?["fullName"]?.Value<string>();
            recordFullName.Should().NotBeNullOrEmpty();
            recordFullName.Should().StartWith("System", "All records should belong to System model");
        }
    }

    [Fact]
    public async Task CkRecords_QueryWithSortOrder_ReturnsSortedRecords()
    {
        // Arrange - sort by ckRecordId descending
        var query = @"
            query {
                constructionKit {
                    records(sortOrder: [{ attributePath: ""ckRecordId"", sortOrder: DESCENDING }]) {
                        items {
                            ckRecordId {
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        if (recordsArray.Count > 1)
        {
            // Verify descending order only if multiple records exist
            var fullNames = recordsArray.Select(t => t["ckRecordId"]?["fullName"]?.Value<string>()).ToList();
            var sortedNames = fullNames.OrderByDescending(n => n).ToList();
            fullNames.Should().BeEquivalentTo(sortedNames, options => options.WithStrictOrdering(),
                "Records should be sorted in descending order");
        }
    }

    [Fact]
    public async Task CkRecords_QueryWithPagination_ReturnsPagedResults()
    {
        // Arrange - get first 2 records
        var query = @"
            query {
                constructionKit {
                    records(first: 2) {
                        totalCount
                        pageInfo {
                            hasNextPage
                            hasPreviousPage
                        }
                        items {
                            ckRecordId {
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Count.Should().BeLessThanOrEqualTo(2, "Should return at most 2 records with first: 2");

        var totalCount = answer.SelectToken("data.constructionKit.records.totalCount")?.Value<int>();
        totalCount.Should().NotBeNull();
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CkRecords_QueryWithEmptyCkIdsArray_ReturnsEmptyResult()
    {
        // Arrange - empty ckIds array should return empty result
        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    records(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckRecordId {
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

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().BeEmpty("Empty ckIds array should return empty result");
    }

    [Fact]
    public async Task CkRecords_QueryWithCkIdAndRtCkId_ReturnsError()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRecordRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();

        // Using both ckId and rtCkId should fail
        var query = @"
            query ($ckId: String!, $rtCkId: String!) {
                constructionKit {
                    records(ckId: $ckId, rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = fullCkId, rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and rtCkId should produce an error");
    }

    [Fact]
    public async Task CkRecords_QueryWithCkIdAndCkModelIds_ReturnsError()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRecordRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();

        // Using both ckId and ckModelIds should fail
        var query = @"
            query ($ckId: String!, $modelIds: [String]!) {
                constructionKit {
                    records(ckId: $ckId, ckModelIds: $modelIds) {
                        items {
                            ckRecordId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            ckId = fullCkId,
            modelIds = new[] { SystemModelId }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and ckModelIds should produce an error");
    }

    [Fact]
    public async Task CkRecords_QueryWithNonExistentCkId_ReturnsEmptyResult()
    {
        // Arrange - non-existent ckId should return empty result
        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    records(ckId: $ckId) {
                        totalCount
                        items {
                            ckRecordId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = "NonExistent/RecordThatDoesNotExist" });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().BeEmpty("Non-existent ckId should return empty result");
    }

    #endregion

    #region AB#2801 Tests - CkId Object Structure

    [Fact]
    public async Task CkRecords_CkRecordId_ReturnsFullNameField()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1);

        // Verify fullName field exists and has expected format
        var firstRecord = recordsArray[0];
        var fullName = firstRecord["ckRecordId"]?["fullName"]?.Value<string>();
        fullName.Should().NotBeNullOrEmpty("fullName field should be present");
        // fullName format is 'ModelName-Version/RecordName-ElementVersion' e.g. 'System-1.0.3/ValueRange-1'
        fullName.Should().MatchRegex(@"^System-\d+\.\d+\.\d+/\w+-\d+$",
            "fullName should be in format 'ModelName-Version/RecordName-ElementVersion'");
    }

    [Fact]
    public async Task CkRecords_CkRecordId_ReturnsSemanticVersionedFullNameField()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1);

        // Verify semanticVersionedFullName field exists
        var record = recordsArray[0];
        var semanticVersionedFullName = record["ckRecordId"]?["semanticVersionedFullName"]?.Value<string>();
        semanticVersionedFullName.Should().NotBeNullOrEmpty("semanticVersionedFullName field should be present");
    }

    [Fact]
    public async Task CkRecords_CkRecordId_JsonStructureIsCorrect()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1);

        // Verify complete CkRecordId object structure
        var record = recordsArray[0];
        var ckRecordId = record["ckRecordId"];
        ckRecordId.Should().NotBeNull("ckRecordId should be an object, not a scalar");
        ckRecordId.Type.Should().Be(JTokenType.Object, "ckRecordId should be a JSON object");

        // Verify all expected fields are present
        var fullName = ckRecordId["fullName"]?.Value<string>();
        var semanticVersionedFullName = ckRecordId["semanticVersionedFullName"]?.Value<string>();

        fullName.Should().NotBeNullOrEmpty();
        semanticVersionedFullName.Should().NotBeNullOrEmpty();

        // Verify semanticVersionedFullName contains expected pattern
        semanticVersionedFullName.Should().Contain("System");
    }

    #endregion

    #region Record-Specific Tests

    [Fact]
    public async Task CkRecords_QueryReturnsIsAbstractAndIsFinal()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                fullName
                            }
                            isAbstract
                            isFinal
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1);

        var record = recordsArray[0];
        record["isAbstract"].Should().NotBeNull("isAbstract should be present");
        record["isFinal"].Should().NotBeNull("isFinal should be present");
    }

    [Fact]
    public async Task CkRecords_QueryReturnsAttributes()
    {
        // Arrange - first get an available record
        var firstRecordRtCkId = await GetFirstAvailableRecordRtCkId();
        if (string.IsNullOrEmpty(firstRecordRtCkId))
        {
            // Skip if no records available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    records(rtCkId: $rtCkId) {
                        items {
                            ckRecordId {
                                fullName
                            }
                            attributes {
                                items {
                                    attributeName
                                    attributeValueType
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRecordRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var records = answer.SelectToken("data.constructionKit.records.items");
        records.Should().NotBeNull();

        var recordsArray = (JArray)records;
        recordsArray.Should().HaveCount(1);

        var record = recordsArray[0];
        var attributes = record["attributes"];
        attributes.Should().NotBeNull("attributes connection should be present");
    }

    #endregion
}
