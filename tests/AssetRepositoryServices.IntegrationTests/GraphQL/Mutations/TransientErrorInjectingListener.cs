using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
///     Added to the options before execution, so it runs before <see cref="OctoSessionListener" />: its
///     <c>AfterExecutionAsync</c> adds an error to the first <c>failAttempts</c> attempts after the resolvers wrote,
///     and <see cref="OctoSessionListener" /> then aborts the transaction.
/// </summary>
internal sealed class TransientErrorInjectingListener(Func<int, Exception> error, int failAttempts = 1)
    : IDocumentExecutionListener
{
    public int Attempts { get; private set; }

    public List<int> OctoSessionListenersPerAttempt { get; } = [];

    /// <summary>A MongoDB exception with the label <c>TransientTransactionError</c>.</summary>
    public static MongoException TransientException()
    {
        var exception = new MongoException("injected write conflict");
        exception.AddErrorLabel(TransientTransactionRetry.TransientTransactionErrorLabel);
        return exception;
    }

    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult) =>
        Task.CompletedTask;

    public Task BeforeExecutionAsync(IExecutionContext context)
    {
        Attempts++;
        OctoSessionListenersPerAttempt.Add(context.Listeners.OfType<OctoSessionListener>().Count());
        return Task.CompletedTask;
    }

    public Task AfterExecutionAsync(IExecutionContext context)
    {
        if (Attempts <= failAttempts)
        {
            context.Errors.Add(new ExecutionError("injected", error(Attempts)));
        }

        return Task.CompletedTask;
    }
}
