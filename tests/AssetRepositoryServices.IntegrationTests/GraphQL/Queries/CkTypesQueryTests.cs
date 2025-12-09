using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the ConstructionKit Types query (CkQuery.Types).
/// Tests the GraphQL schema directly without HTTP.
/// Uses only the System model which is always available.
/// </summary>
[Collection("Sequential")]
public class CkTypesQueryTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    // Well-known System model types for testing (rtCkId format - without version)
    private const string SystemModelId = "System";
    private const string EntityTypeRtCkId = "System/Entity";
    private const string TenantTypeRtCkId = "System/Tenant";
    private const string QueryTypeRtCkId = "System/Query";

    public CkTypesQueryTests(CkQueryTestFixture fixture, ITestOutputHelper output)
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
                    types(rtCkId: ""{rtCkId}"") {{
                        items {{
                            ckTypeId {{
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
        return answer.SelectToken("data.constructionKit.types.items[0].ckTypeId.fullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the fully qualified ModelId from a CkTypeId fullName
    /// </summary>
    private string GetModelIdFromFullCkId(string fullCkId)
    {
        // fullCkId format is "System-1.0.3/Entity-1", ModelId is "System-1.0.3"
        return fullCkId.Split('/')[0];
    }

    #region Happy Path Tests

    [Fact]
    public async Task CkTypes_QueryAll_ReturnsSystemModelTypes()
    {
        // Arrange
        var query = @"
            query {
                constructionKit {
                    types {
                        totalCount
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull("Path data.constructionKit.types.items should exist");
        types.Type.Should().Be(JTokenType.Array);

        var typesArray = (JArray)types;
        typesArray.Should().HaveCountGreaterThan(0, "System model should have types");

        // Verify totalCount is set
        var totalCount = answer.SelectToken("data.constructionKit.types.totalCount");
        totalCount.Should().NotBeNull();
        totalCount.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CkTypes_QueryByCkId_ReturnsSingleType()
    {
        // Arrange - first get the full ckId (with version) using rtCkId
        var fullCkId = await GetFullCkIdForRtCkId(EntityTypeRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Entity type");

        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    types(ckId: $ckId) {
                        totalCount
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1, "Query with ckId should return exactly one type");

        var entityType = typesArray[0];
        entityType["ckTypeId"]?["fullName"]?.Value<string>().Should().Contain("Entity");
        entityType["isAbstract"]?.Value<bool>().Should().BeTrue("Entity is an abstract type");
    }

    [Fact]
    public async Task CkTypes_QueryByCkIds_ReturnsMultipleTypes()
    {
        // Arrange - first get the full ckIds (with version) using rtCkId
        var entityFullCkId = await GetFullCkIdForRtCkId(EntityTypeRtCkId);
        var tenantFullCkId = await GetFullCkIdForRtCkId(TenantTypeRtCkId);
        entityFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Entity type");
        tenantFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Tenant type");

        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    types(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckIds = new[] { entityFullCkId, tenantFullCkId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(2, "Query with two ckIds should return exactly two types");

        var fullNames = typesArray.Select(t => t["ckTypeId"]?["fullName"]?.Value<string>()).ToList();
        fullNames.Should().Contain(s => s != null && s.Contains("Entity"));
        fullNames.Should().Contain(s => s != null && s.Contains("Tenant"));
    }

    [Fact]
    public async Task CkTypes_QueryByRtCkId_ReturnsSingleType()
    {
        // Arrange - rtCkId uses the semantic versioned format (without full version)
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                                semanticVersionedFullName
                            }
                            isAbstract
                        }
                    }
                }
            }";

        // rtCkId is the semantic versioned full name (e.g., "System/Entity" without version for latest)
        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1, "Query with rtCkId should return exactly one type");
    }

    [Fact]
    public async Task CkTypes_QueryByRtCkIds_ReturnsMultipleTypes()
    {
        // Arrange
        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    types(rtCkIds: $rtCkIds) {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkIds = new[] { EntityTypeRtCkId, QueryTypeRtCkId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(2, "Query with two rtCkIds should return exactly two types");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task CkTypes_QueryWithCkModelIdsFilter_ReturnsFilteredTypes()
    {
        // Arrange - first get the fully qualified ModelId from an existing type
        var fullCkId = await GetFullCkIdForRtCkId(EntityTypeRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Entity type");
        var systemModelId = GetModelIdFromFullCkId(fullCkId);

        var query = @"
            query ($modelIds: [String]!) {
                constructionKit {
                    types(ckModelIds: $modelIds) {
                        totalCount
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCountGreaterThan(0, "System model should have types");

        // All returned types should belong to the System model
        foreach (var type in typesArray)
        {
            var fullName = type["ckTypeId"]?["fullName"]?.Value<string>();
            fullName.Should().NotBeNullOrEmpty();
            fullName.Should().StartWith("System", "All types should belong to System model");
        }
    }

    [Fact(Skip = "SearchFilter requires MongoDB text index which is not available in test container")]
    public async Task CkTypes_QueryWithSearchFilter_ReturnsMatchingTypes()
    {
        // Note: This test requires a MongoDB text index on the CkType collection
        // which is not created in the integration test database container.

        // Arrange - search for types containing "Tenant"
        var query = @"
            query {
                constructionKit {
                    types(searchFilter: { searchTerm: ""Tenant"", language: ""en"" }) {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                            }
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCountGreaterThan(0, "Should find types matching 'Tenant'");

        // At least one type should contain "Tenant" in fullName
        var fullNames = typesArray.Select(t => t["ckTypeId"]?["fullName"]?.Value<string>()).ToList();
        fullNames.Should().Contain(s => s != null && s.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CkTypes_QueryWithSortOrder_ReturnsSortedTypes()
    {
        // Arrange - sort by ckTypeId descending
        var query = @"
            query {
                constructionKit {
                    types(sortOrder: [{ attributePath: ""ckTypeId"", sortOrder: DESCENDING }]) {
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCountGreaterThan(1, "Need multiple types to verify sorting");

        // Verify descending order
        var fullNames = typesArray.Select(t => t["ckTypeId"]?["fullName"]?.Value<string>()).ToList();
        var sortedNames = fullNames.OrderByDescending(n => n).ToList();
        fullNames.Should().BeEquivalentTo(sortedNames, options => options.WithStrictOrdering(),
            "Types should be sorted in descending order");
    }

    [Fact]
    public async Task CkTypes_QueryWithPagination_ReturnsPagedResults()
    {
        // Arrange - get first 2 types
        var query = @"
            query {
                constructionKit {
                    types(first: 2) {
                        totalCount
                        pageInfo {
                            hasNextPage
                            hasPreviousPage
                        }
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(2, "Should return exactly 2 types with first: 2");

        var totalCount = answer.SelectToken("data.constructionKit.types.totalCount")?.Value<int>();
        totalCount.Should().BeGreaterThan(2, "Total count should be greater than page size");

        var hasNextPage = answer.SelectToken("data.constructionKit.types.pageInfo.hasNextPage")?.Value<bool>();
        hasNextPage.Should().BeTrue("Should have next page when more types exist");
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CkTypes_QueryWithEmptyCkId_ReturnsErrorOrEmptyResult()
    {
        // Arrange - empty string ckId should return an error (invalid CkId format)
        var query = @"
            query {
                constructionKit {
                    types(ckId: """") {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert - either errors or empty result is acceptable for invalid ckId
        result.Should().NotBeNull();

        if (result.Errors != null && result.Errors.Any())
        {
            // Empty string results in an error (invalid CkId format)
            result.Errors.Should().NotBeEmpty("Empty ckId string should produce an error");
        }
        else
        {
            // Or empty result
            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var types = answer.SelectToken("data.constructionKit.types.items");
            types.Should().NotBeNull();

            var typesArray = (JArray)types;
            typesArray.Should().BeEmpty("Empty ckId should return empty result");
        }
    }

    [Fact]
    public async Task CkTypes_QueryWithEmptyCkIdsArray_ReturnsEmptyResult()
    {
        // Arrange - empty ckIds array should return empty result
        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    types(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckTypeId {
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

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().BeEmpty("Empty ckIds array should return empty result");
    }

    [Fact]
    public async Task CkTypes_QueryWithCkIdAndRtCkId_ReturnsError()
    {
        // Arrange - first get the full ckId (with version) using rtCkId
        var entityFullCkId = await GetFullCkIdForRtCkId(EntityTypeRtCkId);
        entityFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Entity type");

        // Using both ckId and rtCkId should fail
        var query = @"
            query ($ckId: String!, $rtCkId: String!) {
                constructionKit {
                    types(ckId: $ckId, rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = entityFullCkId, rtCkId = TenantTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and rtCkId should produce an error");
    }

    [Fact]
    public async Task CkTypes_QueryWithCkIdAndCkModelIds_ReturnsError()
    {
        // Arrange - first get the full ckId (with version) using rtCkId
        var entityFullCkId = await GetFullCkIdForRtCkId(EntityTypeRtCkId);
        entityFullCkId.Should().NotBeNullOrEmpty("Should be able to resolve Entity type");

        // Using both ckId and ckModelIds should fail
        var query = @"
            query ($ckId: String!, $modelIds: [String]!) {
                constructionKit {
                    types(ckId: $ckId, ckModelIds: $modelIds) {
                        items {
                            ckTypeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            ckId = entityFullCkId,
            modelIds = new[] { SystemModelId }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and ckModelIds should produce an error");
    }

    [Fact]
    public async Task CkTypes_QueryWithNonExistentCkId_ReturnsEmptyResult()
    {
        // Arrange - non-existent ckId should return empty result
        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    types(ckId: $ckId) {
                        totalCount
                        items {
                            ckTypeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = "NonExistent/TypeThatDoesNotExist" });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().BeEmpty("Non-existent ckId should return empty result");
    }

    #endregion

    #region AB#2801 Tests - CkId Object Structure

    [Fact]
    public async Task CkTypes_CkTypeId_ReturnsFullNameField()
    {
        // Arrange - verify the new CkId object structure with fullName using rtCkId
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        // Verify fullName field exists and has expected format
        var firstType = typesArray[0];
        var fullName = firstType["ckTypeId"]?["fullName"]?.Value<string>();
        fullName.Should().NotBeNullOrEmpty("fullName field should be present");
        // fullName format is 'ModelName-Version/TypeName-ElementVersion' e.g. 'System-1.0.3/Entity-1'
        fullName.Should().MatchRegex(@"^System-\d+\.\d+\.\d+/\w+-\d+$",
            "fullName should be in format 'ModelName-Version/TypeName-ElementVersion'");
    }

    [Fact]
    public async Task CkTypes_CkTypeId_ReturnsSemanticVersionedFullNameField()
    {
        // Arrange - verify the new CkId object structure with semanticVersionedFullName using rtCkId
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        // Verify semanticVersionedFullName field exists
        var entityType = typesArray[0];
        var semanticVersionedFullName = entityType["ckTypeId"]?["semanticVersionedFullName"]?.Value<string>();
        semanticVersionedFullName.Should().NotBeNullOrEmpty("semanticVersionedFullName field should be present");
        semanticVersionedFullName.Should().Contain("Entity");
    }

    [Fact]
    public async Task CkTypes_CkTypeId_JsonStructureIsCorrect()
    {
        // Arrange - verify the complete JSON structure of the CkId object using rtCkId
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = TenantTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        // Verify complete CkTypeId object structure
        var tenantType = typesArray[0];
        var ckTypeId = tenantType["ckTypeId"];
        ckTypeId.Should().NotBeNull("ckTypeId should be an object, not a scalar");
        ckTypeId.Type.Should().Be(JTokenType.Object, "ckTypeId should be a JSON object");

        // Verify all expected fields are present
        var fullName = ckTypeId["fullName"]?.Value<string>();
        var semanticVersionedFullName = ckTypeId["semanticVersionedFullName"]?.Value<string>();

        fullName.Should().NotBeNullOrEmpty();
        semanticVersionedFullName.Should().NotBeNullOrEmpty();

        // Verify semanticVersionedFullName contains expected pattern
        semanticVersionedFullName.Should().Contain("System");
        semanticVersionedFullName.Should().Contain("Tenant");
    }

    [Fact]
    public async Task CkTypes_CkTypeId_CompareFullNameFormats()
    {
        // Arrange - compare fullName and semanticVersionedFullName formats
        var query = @"
            query {
                constructionKit {
                    types(first: 5) {
                        items {
                            ckTypeId {
                                fullName
                                semanticVersionedFullName
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

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;

        foreach (var type in typesArray)
        {
            var fullName = type["ckTypeId"]?["fullName"]?.Value<string>();
            var semanticVersionedFullName = type["ckTypeId"]?["semanticVersionedFullName"]?.Value<string>();

            fullName.Should().NotBeNullOrEmpty();
            semanticVersionedFullName.Should().NotBeNullOrEmpty();

            // fullName should contain version number (e.g., "System-1.0.3/Entity-1")
            fullName.Should().MatchRegex(@"-\d+\.\d+\.\d+/", "fullName should contain version number");

            // fullName format is "Model-x.y.z/TypeName-n" e.g. "System-1.0.3/Entity-1"
            // semanticVersionedFullName format is "Model/TypeName" e.g. "System/Entity"
            // Extract base type name from fullName (strip version suffix like "-1")
            var typePartWithVersion = fullName.Split('/').Last(); // e.g., "Entity-1"
            var typeNameBase = typePartWithVersion.Contains('-')
                ? typePartWithVersion.Substring(0, typePartWithVersion.LastIndexOf('-'))
                : typePartWithVersion;

            var semanticTypeName = semanticVersionedFullName.Split('/').Last();
            semanticTypeName.Should().Be(typeNameBase,
                "Both formats should reference the same type name");
        }
    }

    #endregion

    #region Associations Tests

    [Fact]
    public async Task CkTypes_Associations_ReturnsAssociationsStructure()
    {
        // Arrange - query Entity type which should have associations
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                in {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        originCkTypeId {
                                            fullName
                                        }
                                        targetCkTypeId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                }
                                out {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        originCkTypeId {
                                            fullName
                                        }
                                        targetCkTypeId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        var entityType = typesArray[0];
        var associations = entityType["associations"];
        associations.Should().NotBeNull("associations field should exist");

        // Verify in and out directions exist
        var inAssociations = associations["in"];
        var outAssociations = associations["out"];
        inAssociations.Should().NotBeNull("in direction should exist");
        outAssociations.Should().NotBeNull("out direction should exist");

        // Verify all field exists in both directions
        inAssociations["all"].Should().NotBeNull("in.all should exist");
        outAssociations["all"].Should().NotBeNull("out.all should exist");
    }

    [Fact]
    public async Task CkTypes_Associations_InDirection_ReturnsAllInheritedAndOwned()
    {
        // Arrange - query Entity type for inbound associations
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                in {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                    inherited {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                    owned {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        var entityType = typesArray[0];
        var inAssociations = entityType["associations"]?["in"];
        inAssociations.Should().NotBeNull();

        // Verify all three association sources exist
        var all = inAssociations["all"];
        var inherited = inAssociations["inherited"];
        var owned = inAssociations["owned"];

        all.Should().NotBeNull("all should exist");
        all.Type.Should().Be(JTokenType.Array, "all should be an array");

        inherited.Should().NotBeNull("inherited should exist");
        inherited.Type.Should().Be(JTokenType.Array, "inherited should be an array");

        owned.Should().NotBeNull("owned should exist");
        owned.Type.Should().Be(JTokenType.Array, "owned should be an array");
    }

    [Fact]
    public async Task CkTypes_Associations_OutDirection_ReturnsAllInheritedAndOwned()
    {
        // Arrange - query Entity type for outbound associations
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                out {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                    inherited {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                    owned {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items");
        types.Should().NotBeNull();

        var typesArray = (JArray)types;
        typesArray.Should().HaveCount(1);

        var entityType = typesArray[0];
        var outAssociations = entityType["associations"]?["out"];
        outAssociations.Should().NotBeNull();

        // Verify all three association sources exist
        var all = outAssociations["all"];
        var inherited = outAssociations["inherited"];
        var owned = outAssociations["owned"];

        all.Should().NotBeNull("all should exist");
        all.Type.Should().Be(JTokenType.Array, "all should be an array");

        inherited.Should().NotBeNull("inherited should exist");
        inherited.Type.Should().Be(JTokenType.Array, "inherited should be an array");

        owned.Should().NotBeNull("owned should exist");
        owned.Type.Should().Be(JTokenType.Array, "owned should be an array");
    }

    [Fact]
    public async Task CkTypes_Associations_AssociationFields_HaveCorrectStructure()
    {
        // Arrange - query for type with associations to verify association field structure
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                out {
                                    all {
                                        roleId {
                                            fullName
                                            semanticVersionedFullName
                                        }
                                        originCkTypeId {
                                            fullName
                                            semanticVersionedFullName
                                        }
                                        targetCkTypeId {
                                            fullName
                                            semanticVersionedFullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associations = answer.SelectToken("data.constructionKit.types.items[0].associations.out.all") as JArray;
        associations.Should().NotBeNull();

        // If there are associations, verify their structure
        if (associations.Count > 0)
        {
            var firstAssociation = associations[0];

            // Verify roleId structure
            var roleId = firstAssociation["roleId"];
            roleId.Should().NotBeNull("roleId should exist");
            roleId["fullName"]?.Value<string>().Should().NotBeNullOrEmpty("roleId.fullName should exist");
            roleId["semanticVersionedFullName"]?.Value<string>().Should().NotBeNullOrEmpty("roleId.semanticVersionedFullName should exist");

            // Verify originCkTypeId structure
            var originCkTypeId = firstAssociation["originCkTypeId"];
            originCkTypeId.Should().NotBeNull("originCkTypeId should exist");
            originCkTypeId["fullName"]?.Value<string>().Should().NotBeNullOrEmpty("originCkTypeId.fullName should exist");

            // Verify targetCkTypeId structure
            var targetCkTypeId = firstAssociation["targetCkTypeId"];
            targetCkTypeId.Should().NotBeNull("targetCkTypeId should exist");
            targetCkTypeId["fullName"]?.Value<string>().Should().NotBeNullOrEmpty("targetCkTypeId.fullName should exist");

            // Verify navigationPropertyName
            var navigationPropertyName = firstAssociation["navigationPropertyName"]?.Value<string>();
            navigationPropertyName.Should().NotBeNullOrEmpty("navigationPropertyName should exist");

            // Verify multiplicity
            var multiplicity = firstAssociation["multiplicity"]?.Value<string>();
            multiplicity.Should().NotBeNullOrEmpty("multiplicity should exist");
        }
    }

    [Fact]
    public async Task CkTypes_Associations_AllContainsInheritedAndOwned()
    {
        // Arrange - verify that 'all' contains items from both 'inherited' and 'owned'
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                out {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                    }
                                    inherited {
                                        roleId {
                                            fullName
                                        }
                                    }
                                    owned {
                                        roleId {
                                            fullName
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var outAssociations = answer.SelectToken("data.constructionKit.types.items[0].associations.out");
        outAssociations.Should().NotBeNull();

        var all = outAssociations["all"] as JArray ?? new JArray();
        var inherited = outAssociations["inherited"] as JArray ?? new JArray();
        var owned = outAssociations["owned"] as JArray ?? new JArray();

        // 'all' should contain the union of 'inherited' and 'owned'
        var allCount = all.Count;
        var inheritedAndOwnedCount = inherited.Count + owned.Count;

        allCount.Should().Be(inheritedAndOwnedCount,
            "all associations count should equal inherited + owned count");

        // Extract roleIds from each list
        var allRoleIds = all.Select(a => a["roleId"]?["fullName"]?.Value<string>()).ToHashSet();
        var inheritedRoleIds = inherited.Select(a => a["roleId"]?["fullName"]?.Value<string>()).ToHashSet();
        var ownedRoleIds = owned.Select(a => a["roleId"]?["fullName"]?.Value<string>()).ToHashSet();

        // All inherited and owned should be present in all
        foreach (var roleId in inheritedRoleIds)
        {
            allRoleIds.Should().Contain(roleId, "inherited associations should be in all");
        }

        foreach (var roleId in ownedRoleIds)
        {
            allRoleIds.Should().Contain(roleId, "owned associations should be in all");
        }
    }

    [Fact]
    public async Task CkTypes_Associations_MultiplicityValues_AreValid()
    {
        // Arrange - verify multiplicity values are valid enum values
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            associations {
                                in {
                                    all {
                                        multiplicity
                                    }
                                }
                                out {
                                    all {
                                        multiplicity
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var entityType = answer.SelectToken("data.constructionKit.types.items[0]");
        entityType.Should().NotBeNull();

        // Valid multiplicity values: 0_1 (zero or one), 1 (one), N (zero or more), 1_N (one or more)
        var validMultiplicities = new[] { "0_1", "1", "N", "1_N" };

        // Check inbound multiplicities
        var inAll = entityType["associations"]?["in"]?["all"] as JArray ?? new JArray();
        foreach (var association in inAll)
        {
            var multiplicity = association["multiplicity"]?.Value<string>();
            if (multiplicity != null)
            {
                validMultiplicities.Should().Contain(multiplicity,
                    $"multiplicity '{multiplicity}' should be a valid enum value");
            }
        }

        // Check outbound multiplicities
        var outAll = entityType["associations"]?["out"]?["all"] as JArray ?? new JArray();
        foreach (var association in outAll)
        {
            var multiplicity = association["multiplicity"]?.Value<string>();
            if (multiplicity != null)
            {
                validMultiplicities.Should().Contain(multiplicity,
                    $"multiplicity '{multiplicity}' should be a valid enum value");
            }
        }
    }

    [Fact]
    public async Task CkTypes_Associations_QueryOnlyInDirection()
    {
        // Arrange - verify we can query only inbound direction
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                in {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associations = answer.SelectToken("data.constructionKit.types.items[0].associations");
        associations.Should().NotBeNull();

        // Only 'in' direction should be present in the response
        associations["in"].Should().NotBeNull("in direction should exist");
        // 'out' is not queried, so it should not be in the response
    }

    [Fact]
    public async Task CkTypes_Associations_QueryOnlyOutDirection()
    {
        // Arrange - verify we can query only outbound direction
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                out {
                                    all {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associations = answer.SelectToken("data.constructionKit.types.items[0].associations");
        associations.Should().NotBeNull();

        // Only 'out' direction should be present in the response
        associations["out"].Should().NotBeNull("out direction should exist");
    }

    [Fact]
    public async Task CkTypes_Associations_QueryOnlyOwned()
    {
        // Arrange - verify we can query only owned associations
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                out {
                                    owned {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = EntityTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var outAssociations = answer.SelectToken("data.constructionKit.types.items[0].associations.out");
        outAssociations.Should().NotBeNull();

        var owned = outAssociations["owned"];
        owned.Should().NotBeNull("owned should exist");
        owned.Type.Should().Be(JTokenType.Array, "owned should be an array");
    }

    [Fact]
    public async Task CkTypes_Associations_QueryOnlyInherited()
    {
        // Arrange - verify we can query only inherited associations
        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    types(rtCkId: $rtCkId) {
                        items {
                            ckTypeId {
                                fullName
                            }
                            associations {
                                in {
                                    inherited {
                                        roleId {
                                            fullName
                                        }
                                        navigationPropertyName
                                        multiplicity
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = TenantTypeRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var inAssociations = answer.SelectToken("data.constructionKit.types.items[0].associations.in");
        inAssociations.Should().NotBeNull();

        var inherited = inAssociations["inherited"];
        inherited.Should().NotBeNull("inherited should exist");
        inherited.Type.Should().Be(JTokenType.Array, "inherited should be an array");
    }

    [Fact]
    public async Task CkTypes_Associations_MultipleTypesWithAssociations()
    {
        // Arrange - query multiple types and their associations
        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    types(rtCkIds: $rtCkIds) {
                        items {
                            ckTypeId {
                                semanticVersionedFullName
                            }
                            associations {
                                out {
                                    all {
                                        roleId {
                                            semanticVersionedFullName
                                        }
                                    }
                                }
                                in {
                                    all {
                                        roleId {
                                            semanticVersionedFullName
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkIds = new[] { EntityTypeRtCkId, TenantTypeRtCkId } });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var types = answer.SelectToken("data.constructionKit.types.items") as JArray;
        types.Should().NotBeNull();
        types.Should().HaveCount(2, "Should return both types");

        foreach (var type in types)
        {
            var associations = type["associations"];
            associations.Should().NotBeNull("Each type should have associations field");

            var outAll = associations["out"]?["all"];
            var inAll = associations["in"]?["all"];

            outAll.Should().NotBeNull("out.all should exist");
            inAll.Should().NotBeNull("in.all should exist");
        }
    }

    #endregion
}
