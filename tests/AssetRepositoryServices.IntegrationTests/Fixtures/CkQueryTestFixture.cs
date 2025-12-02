using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for testing Construction Kit (CK) queries without runtime data.
/// Only initializes the System model which is always available.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CkQueryTestFixture : AssetRepoFixture
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

        if (_serializer == null)
        {
            throw new InvalidOperationException("GraphQL serializer not initialized");
        }

        // Use GraphQL serializer to properly deserialize variables to native types
        Inputs? inputs = null;
        if (variables != null)
        {
            inputs = _serializer.Deserialize<Inputs>(variables);
        }

        var result = await _documentExecuter.ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = query;
            options.Variables = inputs;
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
