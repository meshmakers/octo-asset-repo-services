using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class TenantDocumentExecutor : IDocumentExecuter<OctoSchema>
{
    private readonly IDocumentExecuter _documentExecutor;
    private readonly ISchemaContext _schemaContext;

    public TenantDocumentExecutor(ISchemaContext schemaContext, IDocumentExecuter documentExecutor)
    {
        _schemaContext = schemaContext;
        _documentExecutor = documentExecutor ?? throw new ArgumentNullException(nameof(documentExecutor));
    }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
    {
        if (options.Schema != null)
        {
            throw new InvalidOperationException(
                "ExecutionOptions.Schema must be null when calling this typed IDocumentExecuter<> implementation; it will be pulled from the dependency injection provider.");
        }

        var tenantContext = Helpers.GetTenantContext(options.UserContext);

        options.Schema = await _schemaContext.GetOrCreateAsync(tenantContext.TenantId);
        return await _documentExecutor.ExecuteAsync(options);
    }
}