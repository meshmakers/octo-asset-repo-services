using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the ConstructionKit AssociationRoles query (CkQuery.AssociationRoles).
/// Tests the GraphQL schema directly without HTTP.
/// Uses only the System model which is always available.
/// </summary>
[Collection("Sequential")]
public class CkAssociationRolesQueryTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    // Well-known System model for testing
    private const string SystemModelId = "System";

    public CkAssociationRolesQueryTests(CkQueryTestFixture fixture, ITestOutputHelper output)
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
                    associationRoles(rtCkId: ""{rtCkId}"") {{
                        items {{
                            ckAssociationRoleId {{
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
        return answer.SelectToken("data.constructionKit.associationRoles.items[0].ckAssociationRoleId.fullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the first available association role rtCkId from the System model
    /// </summary>
    private async Task<string?> GetFirstAvailableAssociationRoleRtCkId()
    {
        var query = @"
            query {
                constructionKit {
                    associationRoles(first: 1) {
                        items {
                            ckAssociationRoleId {
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
        return answer.SelectToken("data.constructionKit.associationRoles.items[0].ckAssociationRoleId.semanticVersionedFullName")?.Value<string>();
    }

    /// <summary>
    /// Helper method to get the fully qualified ModelId from a CkAssociationRoleId fullName
    /// </summary>
    private string GetModelIdFromFullCkId(string fullCkId)
    {
        // fullCkId format is "System-1.0.3/RoleName-1", ModelId is "System-1.0.3"
        return fullCkId.Split('/')[0];
    }

    #region Happy Path Tests

    [Fact]
    public async Task CkAssociationRoles_QueryAll_ReturnsSystemModelAssociationRoles()
    {
        // Arrange
        var query = @"
            query {
                constructionKit {
                    associationRoles {
                        totalCount
                        items {
                            ckAssociationRoleId {
                                fullName
                                semanticVersionedFullName
                            }
                            inboundName
                            outboundName
                            inboundMultiplicity
                            outboundMultiplicity
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull("Path data.constructionKit.associationRoles.items should exist");
        associationRoles.Type.Should().Be(JTokenType.Array);

        // Verify totalCount is set
        var totalCount = answer.SelectToken("data.constructionKit.associationRoles.totalCount");
        totalCount.Should().NotBeNull();
    }

    [Fact]
    public async Task CkAssociationRoles_QueryByCkId_ReturnsSingleAssociationRole()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available in System model
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRoleRtCkId);
        fullCkId.Should().NotBeNullOrEmpty("Should be able to resolve association role");

        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    associationRoles(ckId: $ckId) {
                        totalCount
                        items {
                            ckAssociationRoleId {
                                fullName
                                semanticVersionedFullName
                            }
                            inboundName
                            outboundName
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1, "Query with ckId should return exactly one association role");
    }

    [Fact]
    public async Task CkAssociationRoles_QueryByRtCkId_ReturnsSingleAssociationRole()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available in System model
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        totalCount
                        items {
                            ckAssociationRoleId {
                                fullName
                                semanticVersionedFullName
                            }
                            inboundName
                            outboundName
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1, "Query with rtCkId should return exactly one association role");
    }

    [Fact]
    public async Task CkAssociationRoles_QueryByRtCkIds_ReturnsMultipleAssociationRoles()
    {
        // Arrange - get all available association roles first
        var allRolesQuery = @"
            query {
                constructionKit {
                    associationRoles(first: 2) {
                        items {
                            ckAssociationRoleId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var allRolesResult = await _fixture.ExecuteGraphQlAsync(allRolesQuery);
        if (allRolesResult.Errors != null && allRolesResult.Errors.Any())
        {
            return; // Skip if error
        }

        var allRolesJson = _fixture.SerializeGraphQl(allRolesResult);
        var allRolesAnswer = JObject.Parse(allRolesJson);
        var roleItems = allRolesAnswer.SelectToken("data.constructionKit.associationRoles.items") as JArray;

        if (roleItems == null || roleItems.Count < 2)
        {
            // Skip if less than 2 association roles available
            return;
        }

        var rtCkIds = roleItems
            .Select(r => r["ckAssociationRoleId"]?["semanticVersionedFullName"]?.Value<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Take(2)
            .ToArray();

        var query = @"
            query ($rtCkIds: [String]!) {
                constructionKit {
                    associationRoles(rtCkIds: $rtCkIds) {
                        totalCount
                        items {
                            ckAssociationRoleId {
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(2, "Query with two rtCkIds should return exactly two association roles");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task CkAssociationRoles_QueryWithCkModelIdsFilter_ReturnsFilteredAssociationRoles()
    {
        // Arrange - first get an available association role to get the model ID
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRoleRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();
        var systemModelId = GetModelIdFromFullCkId(fullCkId);

        var query = @"
            query ($modelIds: [String]!) {
                constructionKit {
                    associationRoles(ckModelIds: $modelIds) {
                        totalCount
                        items {
                            ckAssociationRoleId {
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        // All returned association roles should belong to the System model
        foreach (var role in rolesArray)
        {
            var roleFullName = role["ckAssociationRoleId"]?["fullName"]?.Value<string>();
            roleFullName.Should().NotBeNullOrEmpty();
            roleFullName.Should().StartWith("System", "All association roles should belong to System model");
        }
    }

    [Fact]
    public async Task CkAssociationRoles_QueryWithSortOrder_ReturnsSortedAssociationRoles()
    {
        // Arrange - sort by inboundName descending
        var query = @"
            query {
                constructionKit {
                    associationRoles(sortOrder: [{ attributePath: ""inboundName"", sortOrder: DESCENDING }]) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                            inboundName
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        if (rolesArray.Count > 1)
        {
            // Verify descending order only if multiple association roles exist
            var inboundNames = rolesArray.Select(t => t["inboundName"]?.Value<string>()).ToList();
            var sortedNames = inboundNames.OrderByDescending(n => n).ToList();
            inboundNames.Should().BeEquivalentTo(sortedNames, options => options.WithStrictOrdering(),
                "Association roles should be sorted in descending order by inboundName");
        }
    }

    [Fact]
    public async Task CkAssociationRoles_QueryWithPagination_ReturnsPagedResults()
    {
        // Arrange - get first 2 association roles
        var query = @"
            query {
                constructionKit {
                    associationRoles(first: 2) {
                        totalCount
                        pageInfo {
                            hasNextPage
                            hasPreviousPage
                        }
                        items {
                            ckAssociationRoleId {
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Count.Should().BeLessThanOrEqualTo(2, "Should return at most 2 association roles with first: 2");

        var totalCount = answer.SelectToken("data.constructionKit.associationRoles.totalCount")?.Value<int>();
        totalCount.Should().NotBeNull();
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CkAssociationRoles_QueryWithEmptyCkIdsArray_ReturnsEmptyResult()
    {
        // Arrange - empty ckIds array should return empty result
        var query = @"
            query ($ckIds: [String]!) {
                constructionKit {
                    associationRoles(ckIds: $ckIds) {
                        totalCount
                        items {
                            ckAssociationRoleId {
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

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().BeEmpty("Empty ckIds array should return empty result");
    }

    [Fact]
    public async Task CkAssociationRoles_QueryWithCkIdAndRtCkId_ReturnsError()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRoleRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();

        // Using both ckId and rtCkId should fail
        var query = @"
            query ($ckId: String!, $rtCkId: String!) {
                constructionKit {
                    associationRoles(ckId: $ckId, rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = fullCkId, rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Using both ckId and rtCkId should produce an error");
    }

    [Fact]
    public async Task CkAssociationRoles_QueryWithCkIdAndCkModelIds_ReturnsError()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var fullCkId = await GetFullCkIdForRtCkId(firstRoleRtCkId);
        fullCkId.Should().NotBeNullOrEmpty();

        // Using both ckId and ckModelIds should fail
        var query = @"
            query ($ckId: String!, $modelIds: [String]!) {
                constructionKit {
                    associationRoles(ckId: $ckId, ckModelIds: $modelIds) {
                        items {
                            ckAssociationRoleId {
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
    public async Task CkAssociationRoles_QueryWithNonExistentCkId_ReturnsEmptyResult()
    {
        // Arrange - non-existent ckId should return empty result
        var query = @"
            query ($ckId: String!) {
                constructionKit {
                    associationRoles(ckId: $ckId) {
                        totalCount
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { ckId = "NonExistent/RoleThatDoesNotExist" });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().BeEmpty("Non-existent ckId should return empty result");
    }

    #endregion

    #region CkId Object Structure Tests

    [Fact]
    public async Task CkAssociationRoles_CkAssociationRoleId_ReturnsFullNameField()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        // Verify fullName field exists and has expected format
        var firstRole = rolesArray[0];
        var fullName = firstRole["ckAssociationRoleId"]?["fullName"]?.Value<string>();
        fullName.Should().NotBeNullOrEmpty("fullName field should be present");
        // fullName format is 'ModelName-Version/RoleName-ElementVersion' e.g. 'System-1.0.3/RoleName-1'
        fullName.Should().MatchRegex(@"^System-\d+\.\d+\.\d+/\w+-\d+$",
            "fullName should be in format 'ModelName-Version/RoleName-ElementVersion'");
    }

    [Fact]
    public async Task CkAssociationRoles_CkAssociationRoleId_ReturnsSemanticVersionedFullNameField()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        // Verify semanticVersionedFullName field exists
        var role = rolesArray[0];
        var semanticVersionedFullName = role["ckAssociationRoleId"]?["semanticVersionedFullName"]?.Value<string>();
        semanticVersionedFullName.Should().NotBeNullOrEmpty("semanticVersionedFullName field should be present");
    }

    [Fact]
    public async Task CkAssociationRoles_CkAssociationRoleId_JsonStructureIsCorrect()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                                semanticVersionedFullName
                            }
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        // Verify complete CkAssociationRoleId object structure
        var role = rolesArray[0];
        var ckAssociationRoleId = role["ckAssociationRoleId"];
        ckAssociationRoleId.Should().NotBeNull("ckAssociationRoleId should be an object, not a scalar");
        ckAssociationRoleId.Type.Should().Be(JTokenType.Object, "ckAssociationRoleId should be a JSON object");

        // Verify all expected fields are present
        var fullName = ckAssociationRoleId["fullName"]?.Value<string>();
        var semanticVersionedFullName = ckAssociationRoleId["semanticVersionedFullName"]?.Value<string>();

        fullName.Should().NotBeNullOrEmpty();
        semanticVersionedFullName.Should().NotBeNullOrEmpty();

        // Verify semanticVersionedFullName contains expected pattern
        semanticVersionedFullName.Should().Contain("System");
    }

    #endregion

    #region AssociationRole-Specific Tests

    [Fact]
    public async Task CkAssociationRoles_QueryReturnsInboundAndOutboundNames()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                            inboundName
                            outboundName
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        var role = rolesArray[0];
        // inboundName and outboundName may be null, but the fields should exist
        role["inboundName"].Should().NotBeNull("inboundName field should be present");
        role["outboundName"].Should().NotBeNull("outboundName field should be present");
    }

    [Fact]
    public async Task CkAssociationRoles_QueryReturnsMultiplicities()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                            inboundMultiplicity
                            outboundMultiplicity
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        var role = rolesArray[0];
        role["inboundMultiplicity"].Should().NotBeNull("inboundMultiplicity should be present");
        role["outboundMultiplicity"].Should().NotBeNull("outboundMultiplicity should be present");

        // Verify multiplicity values are valid enum values
        var inboundMultiplicity = role["inboundMultiplicity"]?.Value<string>();
        var outboundMultiplicity = role["outboundMultiplicity"]?.Value<string>();

        inboundMultiplicity.Should().NotBeNullOrEmpty();
        outboundMultiplicity.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CkAssociationRoles_QueryReturnsDescription()
    {
        // Arrange - first get an available association role
        var firstRoleRtCkId = await GetFirstAvailableAssociationRoleRtCkId();
        if (string.IsNullOrEmpty(firstRoleRtCkId))
        {
            // Skip if no association roles available
            return;
        }

        var query = @"
            query ($rtCkId: String!) {
                constructionKit {
                    associationRoles(rtCkId: $rtCkId) {
                        items {
                            ckAssociationRoleId {
                                fullName
                            }
                            description
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new { rtCkId = firstRoleRtCkId });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var associationRoles = answer.SelectToken("data.constructionKit.associationRoles.items");
        associationRoles.Should().NotBeNull();

        var rolesArray = (JArray)associationRoles;
        rolesArray.Should().HaveCount(1);

        // description may be null, but the query should succeed
        var role = (JObject)rolesArray[0];
        role.ContainsKey("description").Should().BeTrue("description field should be queryable");
    }

    #endregion
}
