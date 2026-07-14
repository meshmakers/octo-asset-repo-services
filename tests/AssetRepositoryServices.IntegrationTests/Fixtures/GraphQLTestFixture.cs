using System.Text.Json;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

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
    private IMongoClient? _mongoClient;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        _documentExecuter = Provider?.GetRequiredService<IDocumentExecuter<OctoSchema>>();
        _serializer = Provider?.GetRequiredService<IGraphQLTextSerializer>();
        _mongoClient = new MongoClient(GetConnectionString());
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

    /// <summary>
    /// Reads the raw stored value of an attribute directly from MongoDB, bypassing the GraphQL
    /// enum-to-name resolution. Used to assert on the actually-persisted CLR/BSON type (e.g. that an
    /// enum is stored as its integer key, not a verbatim name string — AB#4391).
    /// </summary>
    /// <param name="rtId">The RtId of the entity</param>
    /// <param name="attributeName">The attribute name (camelCase, as stored under the "attributes" subdocument)</param>
    /// <param name="collectionSuffix">The collection suffix (the base type, e.g. "AssetRepositoryIntegrationTestAsset" for MeteringPoint)</param>
    /// <returns>The raw <see cref="BsonValue"/>, or <see cref="BsonNull.Value"/> when absent.</returns>
    public async Task<BsonValue> ReadRawAttributeValueFromMongoDb(string rtId, string attributeName,
        string collectionSuffix)
    {
        if (_mongoClient == null)
        {
            throw new InvalidOperationException("MongoDB client not initialized");
        }

        var database = _mongoClient.GetDatabase(SystemDatabaseName);
        var collection = database.GetCollection<BsonDocument>($"RtEntity_{collectionSuffix}");

        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(rtId));
        var document = await collection.Find(filter).FirstOrDefaultAsync();
        if (document == null)
        {
            throw new InvalidOperationException($"Document with rtId '{rtId}' not found in 'RtEntity_{collectionSuffix}'.");
        }

        if (document.TryGetValue("attributes", out var attributes)
            && attributes is BsonDocument attributesDoc
            && attributesDoc.TryGetValue(attributeName, out var value))
        {
            return value;
        }

        return BsonNull.Value;
    }

    /// <summary>
    /// Removes an attribute from a MongoDB document to simulate legacy data
    /// that was created before the attribute was added to the schema.
    /// </summary>
    /// <param name="rtId">The RtId of the entity</param>
    /// <param name="attributeName">The attribute name to remove</param>
    /// <param name="collectionSuffix">The collection suffix (e.g., "AssetRepositoryIntegrationTestAsset" for MeteringPoint)</param>
    public async Task RemoveAttributeFromMongoDb(string rtId, string attributeName, string collectionSuffix)
    {
        if (_mongoClient == null)
        {
            throw new InvalidOperationException("MongoDB client not initialized");
        }

        // The database name follows the system tenant convention (not the test tenant)
        // GraphQL queries are executed against the system context
        var database = _mongoClient.GetDatabase(SystemDatabaseName);

        // Runtime entities are stored in collections with "RtEntity_" prefix followed by the base type
        var collectionName = $"RtEntity_{collectionSuffix}";
        var collection = database.GetCollection<BsonDocument>(collectionName);

        // Build filter for the rtId
        var objectId = ObjectId.Parse(rtId);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", objectId);

        // Build update to unset the attribute
        // Attributes are stored in an "attributes" subdocument
        var attributePath = $"attributes.{attributeName}";
        var update = Builders<BsonDocument>.Update.Unset(attributePath);

        var result = await collection.UpdateOneAsync(filter, update);

        if (result.ModifiedCount == 0)
        {
            throw new InvalidOperationException(
                $"Failed to remove attribute '{attributePath}' from document with rtId '{rtId}' in collection '{collectionName}'. " +
                $"Modified count: {result.ModifiedCount}, Matched count: {result.MatchedCount}");
        }
    }
}
