using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Microsoft.Extensions.DependencyInjection;
using JsonSerializer = System.Text.Json.JsonSerializer;

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

        var inputs = variables != null
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(variables)
            : null;

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

    public string SerializeGraphQl(ExecutionResult result)
    {
        if (_serializer == null)
        {
            throw new InvalidOperationException("GraphQL services not initialized");
        }
        return _serializer.Serialize(result);
    }
}
