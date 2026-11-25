using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Executes a GraphQL request against the schema of its tenant. A mutation whose transaction failed with the
///     MongoDB label <c>TransientTransactionError</c> is executed again as a whole - new transaction, every resolver
///     again from the request's variables - up to <see cref="TransientTransactionRetryOptions.MaxAttempts" /> times
///     with a short jittered backoff, and only while <see cref="TransientTransactionRetryOptions.MaxElapsed" /> has not
///     passed since the first attempt started. Which requests qualify: <see cref="TransientTransactionRetry" />.
/// </summary>
internal class TenantDocumentExecutor : IDocumentExecuter<OctoSchema>
{
    private readonly ISchemaContext _schemaContext;
    private readonly IDocumentExecuter _documentExecutor;
    private readonly IOptions<TransientTransactionRetryOptions> _retryOptions;
    private readonly ILogger<TenantDocumentExecutor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;

    public TenantDocumentExecutor(ISchemaContext schemaContext, IDocumentExecuter documentExecutor,
        IOptions<TransientTransactionRetryOptions>? retryOptions = null, ILogger<TenantDocumentExecutor>? logger = null)
        : this(schemaContext, documentExecutor, retryOptions, logger, Task.Delay, TimeProvider.System)
    {
    }

    internal TenantDocumentExecutor(ISchemaContext schemaContext, IDocumentExecuter documentExecutor,
        IOptions<TransientTransactionRetryOptions>? retryOptions, ILogger<TenantDocumentExecutor>? logger,
        Func<TimeSpan, CancellationToken, Task> delay, TimeProvider timeProvider)
    {
        _schemaContext = schemaContext ?? throw new ArgumentNullException(nameof(schemaContext));
        _documentExecutor = documentExecutor ?? throw new ArgumentNullException(nameof(documentExecutor));
        _retryOptions = retryOptions ?? Options.Create(new TransientTransactionRetryOptions());
        _logger = logger ?? NullLogger<TenantDocumentExecutor>.Instance;
        _delay = delay;
        _timeProvider = timeProvider;
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

        // GraphQL.NET's execution configuration (AddDocumentListener, AddValidationRule, AddDataLoader) appends to
        // these on every ExecuteAsync; a retry starts from the options as they came in, so nothing runs twice.
        var snapshot = new OptionsSnapshot(options);
        var maxAttempts = Math.Max(1, _retryOptions.Value.MaxAttempts);
        var maxElapsed = _retryOptions.Value.MaxElapsed;
        var started = _timeProvider.GetTimestamp();

        for (var attempt = 1; ; attempt++)
        {
            TransientTransactionRetry.ResetAttempt(options.UserContext);
            var result = await _documentExecutor.ExecuteAsync(options);

            var retry = attempt < maxAttempts && !options.CancellationToken.IsCancellationRequested &&
                        TransientTransactionRetry.ShouldRetry(options.UserContext, result);
            var elapsed = _timeProvider.GetElapsedTime(started);
            if (retry && elapsed >= maxElapsed)
            {
                // A long transaction the server aborted is not run again; nor is a burst that outlasts the budget.
                _logger.LogWarning(
                    "GraphQL mutation attempt {Attempt} failed with a transient transaction error after {ElapsedMs} ms; not retried, the time budget of {MaxElapsedMs} ms is used up",
                    attempt, (int)elapsed.TotalMilliseconds, (int)maxElapsed.TotalMilliseconds);
                retry = false;
            }

            if (!retry)
            {
                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "GraphQL mutation ended after {Attempts} attempts (transient transaction retry), errors: {ErrorCount}",
                        attempt, result.Errors?.Count ?? 0);
                }

                TransientTransactionRetry.ResetAttempt(options.UserContext);
                return result;
            }

            var delay = TransientTransactionRetry.BackoffDelay(_retryOptions.Value, attempt);
            _logger.LogInformation(
                "GraphQL mutation attempt {Attempt} of {MaxAttempts} failed with a transient transaction error; retrying in {DelayMs} ms",
                attempt, maxAttempts, (int)delay.TotalMilliseconds);

            snapshot.Restore(options);
            if (delay > TimeSpan.Zero)
            {
                await _delay(delay, options.CancellationToken);
            }
        }
    }

    /// <summary>The members of the options that the execution configuration changes per execution.</summary>
    private sealed class OptionsSnapshot(ExecutionOptions options)
    {
        private readonly List<IDocumentExecutionListener> _listeners = [.. options.Listeners];
        private readonly IEnumerable<IValidationRule>? _validationRules = options.ValidationRules;
        private readonly IEnumerable<IValidationRule>? _cachedDocumentValidationRules = options.CachedDocumentValidationRules;
        private readonly GraphQLDocument? _document = options.Document;

        public void Restore(ExecutionOptions target)
        {
            target.Listeners.Clear();
            target.Listeners.AddRange(_listeners);
            target.ValidationRules = _validationRules;
            target.CachedDocumentValidationRules = _cachedDocumentValidationRules;
            target.Document = _document;
        }
    }
}
