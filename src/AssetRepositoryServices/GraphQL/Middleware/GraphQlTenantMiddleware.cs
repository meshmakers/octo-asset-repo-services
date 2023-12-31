using System.Text;
using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQLRequest = GraphQL.Transport.GraphQLRequest;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;

/// <inheritdoc />
// ReSharper disable once ClassNeverInstantiated.Global
internal class GraphQlTenantMiddleware : GraphQLHttpMiddleware<OctoSchema>
{
    private readonly IGraphQLTextSerializer _serializer;

    /// <inheritdoc />
    public GraphQlTenantMiddleware(RequestDelegate next, IGraphQLTextSerializer serializer,
        IDocumentExecuter<OctoSchema> documentExecuter, IServiceScopeFactory serviceScopeFactory,
        GraphQLHttpMiddlewareOptions options, IHostApplicationLifetime hostApplicationLifetime)
        : base(next, serializer, documentExecuter, serviceScopeFactory, options, hostApplicationLifetime)
    {
        _serializer = serializer;
    }
    

    protected override async Task<(GraphQLRequest? SingleRequest, IList<GraphQLRequest?>? BatchRequest)?>
        ReadPostContentAsync(
            HttpContext context, RequestDelegate next, string? mediaType, Encoding? sourceEncoding)
    {
        if (context.Request.HasFormContentType)
        {
            try
            {
                var formCollection = await context.Request.ReadFormAsync(context.RequestAborted);
                return (DeserializeFromFormBody(formCollection), null);
            }
            catch (Exception ex)
            {
                if (!await HandleDeserializationErrorAsync(context, next, ex))
                {
                    throw;
                }

                return null;
            }
        }

        return await base.ReadPostContentAsync(context, next, mediaType, sourceEncoding);
    }


    private GraphQLRequest DeserializeFromFormBody(IFormCollection formCollection)
    {
        var request = new GraphQLRequest
        {
            Query = formCollection.TryGetValue("query", out var queryValues) ? queryValues[0] : null,
            Variables = formCollection.TryGetValue("variables", out var variablesValues)
                ? _serializer.Deserialize<Inputs>(variablesValues[0])
                : null,
            Extensions = formCollection.TryGetValue("extensions", out var extensionsValues)
                ? _serializer.Deserialize<Inputs>(extensionsValues[0])
                : null,
            OperationName = formCollection.TryGetValue("operationName", out var operationNameValues)
                ? operationNameValues[0]
                : null
        };
        if (formCollection.Files.Count > 0)
        {
            var dic = request.Variables != null
                ? new Dictionary<string, object?>(request.Variables)
                : new Dictionary<string, object?>();
            foreach (var file in formCollection.Files)
            {
                dic.Add(file.Name, file);
            }

            request.Variables = new Inputs(dic);
        }

        return request;
    }
}
