using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL;

/// <summary>
/// Helper class for executing GraphQL requests in integration tests.
/// </summary>
public class GraphQLTestHelper
{
    private readonly AssetRepoFixture _fixture;
    private readonly string _tenantId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GraphQLTestHelper(AssetRepoFixture fixture, string? tenantId = null)
    {
        _fixture = fixture;
        // Use system tenant as default since that's where sample data is imported
        _tenantId = tenantId ?? "system";
    }

    /// <summary>
    /// Executes a GraphQL query against the test tenant.
    /// </summary>
    public async Task<GraphQLResponse<T>> QueryAsync<T>(
        HttpClient httpClient,
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var request = new GraphQLRequest
        {
            Query = query,
            Variables = variables != null
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(variables, JsonOptions))
                : null,
            OperationName = operationName
        };

        var response = await httpClient.PostAsJsonAsync($"/graphql/{_tenantId}", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GraphQLResponse<T>>(JsonOptions);
        return result ?? throw new InvalidOperationException("Failed to deserialize GraphQL response");
    }
}

public class GraphQLRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public Dictionary<string, object?>? Variables { get; set; }

    [JsonPropertyName("operationName")]
    public string? OperationName { get; set; }
}

public class GraphQLResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; set; }

    [JsonIgnore]
    public bool HasErrors => Errors is { Length: > 0 };
}

public class GraphQLError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public object[]? Path { get; set; }
}
