using GraphQL.Execution;
using GraphQL.Validation;

using GraphQLParser.AST;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <inheritdoc />
// ReSharper disable once ClassNeverInstantiated.Global
internal class OctoSessionListener : IDocumentExecutionListener
{
    private readonly IOctoSessionAccessor _accessor;
    private readonly Services.Files.FileSystemService _fileSystem;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="accessor">The accessor of Octo session for a graphql call</param>
    /// <param name="fileSystem">Holds the sibling-name locks released when the session ends (AB#6477)</param>
    public OctoSessionListener(IOctoSessionAccessor accessor, Services.Files.FileSystemService fileSystem)
    {
        _accessor = accessor;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BeforeExecutionAsync(IExecutionContext context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        _accessor.Session = tenantRepository.GetSession(Helpers.GetSecurityContext(context.UserContext));

        // Only start a transaction for mutations. Read-only queries do not need
        // transactional guarantees and can avoid the MongoDB transactionLifetimeLimitSeconds
        // timeout that occurs when complex nested queries (e.g. with multiple DataLoader
        // batches) take longer than the configured limit.
        if (context.Operation?.Operation == OperationType.Mutation)
        {
            _accessor.Session.StartTransaction();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task AfterExecutionAsync(IExecutionContext context)
    {
        if (!_accessor.HasSession)
        {
            return;
        }

        if (context.Operation?.Operation == OperationType.Mutation)
        {
            // AB#6477: the sibling-name locks of the mutation are held until the transaction has ended.
            var session = _accessor.Session;
            try
            {
                if (context.Errors.Count == 0)
                {
                    await _accessor.CommitAsync().ConfigureAwait(false);
                }
                else
                {
                    await _accessor.AbortAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _fileSystem.NameLocks.Release(session);
            }
        }
    }
}