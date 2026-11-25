using GraphQL;
using GraphQLParser.AST;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Options of the whole-request retry of GraphQL mutations whose transaction failed with a MongoDB
///     <c>TransientTransactionError</c>. Section <c>GraphQl:TransientTransactionRetry</c>.
/// </summary>
public class TransientTransactionRetryOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "GraphQl:TransientTransactionRetry";

    /// <summary>
    ///     Attempts in total, the first one included (default 10; 1 turns the retry off). A transaction that
    ///     conflicts with a parallel writer of the same document (e.g. an auto-increment counter) fails at once, so a
    ///     burst of parallel creations needs several retries until each had its turn. With the default delays the
    ///     waits of one request add up to at most about 4.3 s (on average half of it).
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Upper bound of the jittered delay before the first retry (default 20 ms); it doubles per retry.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Upper bound of any single delay (default 1 s).</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Time budget of the retry (default 10 s): no further attempt starts once this much time has passed since the
    ///     first attempt started. A conflict on a counter fails within milliseconds; a transaction the server aborted
    ///     because it ran too long (<c>transactionLifetimeLimitSeconds</c>, whose error also carries
    ///     <c>TransientTransactionError</c>) is not run again. Zero or less: no retry.
    /// </summary>
    public TimeSpan MaxElapsed { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
///     Decides whether a GraphQL request is retried as a whole after its MongoDB transaction failed with the label
///     <c>TransientTransactionError</c>. The transaction owner is <see cref="OctoSessionListener" /> (one
///     transaction per mutation request); <see cref="TenantDocumentExecutor" /> runs the retry loop.
/// </summary>
/// <remarks>
///     <para>
///         A request is eligible only when <see cref="OctoSessionListener" /> started a transaction for it and every
///         top-level field of the mutation is <c>runtime</c>: runtime entity writes stay inside the transaction, and
///         the engine runs its own side effects (notifications, GridFS cleanup) only after commit or rollback
///         (<c>RegisterTransactionCallbacks</c>), so an aborted attempt leaves nothing behind. <c>constructionKit</c>,
///         <c>streamData</c> and <c>blueprints</c> mutations write outside the transaction (CK cache, CrateDB, events)
///         and are never retried. A CK method call opts out at dispatch (<see cref="MarkNotRetryable" />): its handler
///         may call other systems.
///     </para>
///     <para>
///         A commit that ends with <c>UnknownTransactionCommitResult</c> is never re-executed: the transaction may
///         have committed on the server, and running the handler again would write twice. The driver's own commit
///         retry (one retry of a retryable commit error) applies; the error then surfaces as before.
///     </para>
/// </remarks>
internal static class TransientTransactionRetry
{
    /// <summary>MongoDB error label: the whole transaction may be retried.</summary>
    internal const string TransientTransactionErrorLabel = "TransientTransactionError";

    /// <summary>MongoDB error label: the outcome of a commit is unknown.</summary>
    internal const string UnknownTransactionCommitResultLabel = "UnknownTransactionCommitResult";

    /// <summary>Key of the per-attempt state in the GraphQL user context.</summary>
    internal const string StateKey = "octo.transientTransactionRetry";

    private const string RuntimeFieldName = "runtime";

    /// <summary>State of one attempt, kept in the user context (the same object for every attempt of a request).</summary>
    internal sealed class AttemptState
    {
        /// <summary>A transaction was started for this attempt.</summary>
        public bool TransactionStarted { get; set; }

        /// <summary>The attempt may be repeated (runtime-only mutation without a CK method call).</summary>
        public bool RetryEligible { get; set; }
    }

    /// <summary>Starts a new attempt: forgets the state of the previous one.</summary>
    internal static void ResetAttempt(IDictionary<string, object?>? userContext) => userContext?.Remove(StateKey);

    /// <summary>Called by the transaction owner when it started the transaction of a mutation.</summary>
    internal static void MarkTransactionStarted(IDictionary<string, object?>? userContext,
        GraphQLOperationDefinition? operation)
    {
        if (userContext == null)
        {
            return;
        }

        userContext[StateKey] = new AttemptState
        {
            TransactionStarted = true,
            RetryEligible = IsRuntimeOnly(operation)
        };
    }

    /// <summary>
    ///     Marks the running request as not retryable, because it starts a side effect outside the transaction (a CK
    ///     method call). Call it before the side effect starts.
    /// </summary>
    internal static void MarkNotRetryable(IDictionary<string, object?>? userContext)
    {
        if (userContext?.TryGetValue(StateKey, out var value) == true && value is AttemptState state)
        {
            state.RetryEligible = false;
        }
    }

    /// <summary>True when the attempt that produced <paramref name="result" /> may be run again.</summary>
    internal static bool ShouldRetry(IDictionary<string, object?>? userContext, ExecutionResult result)
    {
        if (userContext?.TryGetValue(StateKey, out var value) != true ||
            value is not AttemptState { TransactionStarted: true, RetryEligible: true })
        {
            return false;
        }

        if (result.Errors is not { Count: > 0 } errors)
        {
            return false;
        }

        var transient = false;
        foreach (var error in errors)
        {
            foreach (var mongoException in MongoExceptionsOf(error))
            {
                if (mongoException.HasErrorLabel(UnknownTransactionCommitResultLabel))
                {
                    // The commit may have succeeded: never run the handler again.
                    return false;
                }

                transient |= mongoException.HasErrorLabel(TransientTransactionErrorLabel);
            }
        }

        return transient;
    }

    /// <summary>
    ///     Full-jitter backoff before retry number <paramref name="retry" /> (1 = first retry): a random delay between
    ///     zero and min(<see cref="TransientTransactionRetryOptions.MaxDelay" />, BaseDelay * 2^(retry-1)).
    /// </summary>
    internal static TimeSpan BackoffDelay(TransientTransactionRetryOptions options, int retry)
    {
        var ceiling = options.BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, retry - 1));
        ceiling = Math.Min(ceiling, options.MaxDelay.TotalMilliseconds);
        return ceiling <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    private static bool IsRuntimeOnly(GraphQLOperationDefinition? operation)
    {
        if (operation?.Operation != OperationType.Mutation || operation.SelectionSet.Selections.Count == 0)
        {
            return false;
        }

        // Conservative: fragments on the mutation root are not resolved, such a request is simply not retried.
        return operation.SelectionSet.Selections.All(selection =>
            selection is GraphQLField field &&
            string.Equals(field.Name.StringValue, RuntimeFieldName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<MongoException> MongoExceptionsOf(ExecutionError error)
    {
        var pending = new Stack<Exception>();
        if (error.InnerException != null)
        {
            pending.Push(error.InnerException);
        }

        var depth = 0;
        while (pending.Count > 0 && depth++ < 64)
        {
            var current = pending.Pop();
            if (current is MongoException mongoException)
            {
                yield return mongoException;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException != null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
