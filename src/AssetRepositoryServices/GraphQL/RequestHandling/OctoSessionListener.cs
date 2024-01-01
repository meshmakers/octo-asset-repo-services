using System.Threading.Tasks;
using GraphQL.Execution;
using GraphQL.Validation;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <inheritdoc />
// ReSharper disable once ClassNeverInstantiated.Global
internal class OctoSessionListener : IDocumentExecutionListener
{
    private readonly IOctoSessionAccessor _accessor;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="accessor">The accessor of Octo session for a graphql call</param>
    public OctoSessionListener(IOctoSessionAccessor accessor)
    {
        _accessor = accessor;
    }

    /// <inheritdoc />
    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task BeforeExecutionAsync(IExecutionContext context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        _accessor.Session = await tenantRepository.GetSessionAsync();
        _accessor.Session.StartTransaction();
    }

    /// <inheritdoc />
    public async Task AfterExecutionAsync(IExecutionContext context)
    {
        if (context.Errors.Count == 0)
        {
            await _accessor.Session.CommitTransactionAsync();
        }
        else
        {
            await _accessor.Session.AbortTransactionAsync();
        }
    }
}
