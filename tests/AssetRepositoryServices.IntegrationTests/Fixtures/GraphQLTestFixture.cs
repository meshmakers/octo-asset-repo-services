using System.Text.Json;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for direct GraphQL schema testing without WebApplicationFactory.
/// Tests the GraphQL schema directly against the database.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class GraphQlTestFixture : SampleDataFixture
{
    private IDocumentExecuter<OctoSchema>? _documentExecuter;
    private IGraphQLTextSerializer? _serializer;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        _documentExecuter = Provider?.GetRequiredService<IDocumentExecuter<OctoSchema>>();
        _serializer = Provider?.GetRequiredService<IGraphQLTextSerializer>();
    }

    /// <summary>
    /// Executes a GraphQL query directly against the schema.
    /// </summary>
    public async Task<ExecutionResult> ExecuteGraphQlAsync(string query, string? variables = null)
    {
        if (_documentExecuter == null)
        {
            throw new InvalidOperationException("GraphQL services not initialized");
        }

        Dictionary<string, object?>? inputs = null;
        if (variables != null)
        {
            using var doc = JsonDocument.Parse(variables);
            inputs = ConvertJsonElement(doc.RootElement) as Dictionary<string, object?>;
        }

        var result = await _documentExecuter.ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = query;
            options.Variables = inputs != null ? new Inputs(inputs) : null;
            options.RequestServices = Provider;
            options.UserContext = new GraphQlUserContext(null, GetSystemContext());
        });

        return result;
    }

    /// <summary>
    /// Recursively converts JsonElement to native .NET types that GraphQL.NET can process.
    /// </summary>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    public string SerializeGraphQl(ExecutionResult result)
    {
        if (_serializer == null)
        {
            throw new InvalidOperationException("GraphQL services not initialized");
        }
        return _serializer.Serialize(result);
    }
}
