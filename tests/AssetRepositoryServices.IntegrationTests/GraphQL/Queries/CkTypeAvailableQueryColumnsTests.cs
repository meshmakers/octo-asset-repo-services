using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for the availableQueryColumns field on CkType.
/// Tests the description field, attributeValueType filter, and searchTerm filter.
/// Uses the System model which is always available via CkQueryTestFixture.
/// </summary>
[Collection("Sequential")]
public class CkTypeAvailableQueryColumnsTests : IClassFixture<CkQueryTestFixture>
{
    private readonly CkQueryTestFixture _fixture;

    public CkTypeAvailableQueryColumnsTests(CkQueryTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private async Task<JArray> QueryAvailableColumnsAsync(string extraArgs = "")
    {
        var argsPart = string.IsNullOrEmpty(extraArgs) ? "" : $"({extraArgs})";
        var query = $@"
            query {{
                constructionKit {{
                    types(rtCkId: ""System/Entity"") {{
                        items {{
                            availableQueryColumns{argsPart} {{
                                totalCount
                                edges {{
                                    node {{
                                        attributePath
                                        attributeValueType
                                        description
                                    }}
                                }}
                            }}
                        }}
                    }}
                }}
            }}";

        var result = await _fixture.ExecuteGraphQlAsync(query);
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var edges = answer.SelectToken("data.constructionKit.types.items[0].availableQueryColumns.edges") as JArray;
        edges.Should().NotBeNull("Query should return edges for System/Entity type. JSON: " + json);

        return new JArray(edges!.Select(e => e["node"]));
    }

    [Fact]
    public async Task AvailableQueryColumns_ReturnsDescription()
    {
        var columns = await QueryAvailableColumnsAsync();
        columns.Should().HaveCountGreaterThan(0);

        // System/Entity has system attributes like rtId, ckTypeId — these should have null description
        var rtIdColumn = columns.FirstOrDefault(c => c!["attributePath"]?.Value<string>() == "rtId");
        rtIdColumn.Should().NotBeNull("Expected rtId column to exist");

        // description field should be present in the response (even if null)
        rtIdColumn!["description"].Should().NotBeNull("description field should be present in response");
    }

    [Fact]
    public async Task AvailableQueryColumns_FilterByValueType_String()
    {
        var columns = await QueryAvailableColumnsAsync(@"attributeValueType: STRING");
        columns.Should().HaveCountGreaterThan(0);

        // All returned columns must be STRING type
        foreach (var column in columns)
        {
            column["attributeValueType"]!.Value<string>().Should().Be("STRING");
        }

        // rtId and ckTypeId are STRING system attributes
        var paths = columns.Select(c => c["attributePath"]!.Value<string>()).ToList();
        paths.Should().Contain("rtId");
        paths.Should().Contain("ckTypeId");
    }

    [Fact]
    public async Task AvailableQueryColumns_FilterByValueType_NoMatch()
    {
        var columns = await QueryAvailableColumnsAsync(@"attributeValueType: GEOSPATIAL_POINT");
        columns.Should().BeEmpty("System/Entity should have no GEOSPATIAL_POINT attributes");
    }

    [Fact]
    public async Task AvailableQueryColumns_SearchTerm_MatchesPath()
    {
        // Search for "rtId" which appears in the path of system attribute "rtId"
        var columns = await QueryAvailableColumnsAsync(@"searchTerm: ""rtId""");
        columns.Should().HaveCountGreaterThan(0);

        // All returned items should contain "rtid" in path or description (case-insensitive)
        foreach (var column in columns)
        {
            var path = column["attributePath"]!.Value<string>()!.ToLower();
            var desc = column["description"]?.Value<string>()?.ToLower();
            (path.Contains("rtid") || (desc != null && desc.Contains("rtid"))).Should().BeTrue(
                $"Column '{path}' should match searchTerm 'rtId' in path or description");
        }
    }

    [Fact]
    public async Task AvailableQueryColumns_FilterByValueType_DateTime()
    {
        var columns = await QueryAvailableColumnsAsync(@"attributeValueType: DATE_TIME");
        columns.Should().HaveCountGreaterThan(0);

        // All returned columns must be DATE_TIME type
        foreach (var column in columns)
        {
            column["attributeValueType"]!.Value<string>().Should().Be("DATE_TIME");
        }

        // rtCreationDateTime and rtChangedDateTime are DATE_TIME system attributes
        var paths = columns.Select(c => c["attributePath"]!.Value<string>()).ToList();
        paths.Should().Contain("rtCreationDateTime");
        paths.Should().Contain("rtChangedDateTime");
    }

    [Fact]
    public async Task AvailableQueryColumns_ExistingFiltersStillWork()
    {
        // Verify existing attributePathContains filter still works
        var columns = await QueryAvailableColumnsAsync(@"attributePathContains: ""rtId""");
        columns.Should().HaveCountGreaterThan(0);

        // All paths should contain "rtId"
        foreach (var column in columns)
        {
            column["attributePath"]!.Value<string>()!.ToLower().Should().Contain("rtid");
        }
    }
}
