using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using GraphQL.Validation;
using GraphQLParser;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.RequestHandling;

/// <summary>
///     <see cref="TenantDocumentExecutor" /> executes a runtime mutation again as a whole when its transaction
///     failed with the MongoDB label <c>TransientTransactionError</c> - bounded, jittered, and only for requests that
///     qualify (<see cref="TransientTransactionRetry" />). The inner executor is scripted: each attempt returns the next
///     result and, like GraphQL.NET's execution configuration, appends a listener and a validation rule to the options.
/// </summary>
public class TransientTransactionRetryTests
{
    private const string RuntimeMutation = "mutation { runtime { tickets { create { rtId } } } }";

    [Fact]
    public async Task TransientErrorOnFirstAttempt_IsRetried_AndTheSecondResultIsReturned()
    {
        var success = new ExecutionResult { Executed = true };
        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), success);

        var result = await NewExecutor(inner).ExecuteAsync(NewOptions());

        result.Should().BeSameAs(success);
        inner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task TransientErrorInTheCommitPhase_IsRetried()
    {
        // A commit refused with TransientTransactionError did not commit: the handler may run again. It reaches the
        // result through GraphQL.NET's unhandled-exception path, wrapped once more.
        var commitError = new UnhandledError("Error executing document.",
            new InvalidOperationException("commit", Labelled(TransientTransactionRetry.TransientTransactionErrorLabel)));
        var inner = new ScriptedExecuter(RuntimeMutation, Result(commitError), new ExecutionResult());

        await NewExecutor(inner).ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task ErrorWithoutTheLabel_IsNotRetried()
    {
        var failure = Result(new ExecutionError("An error occurred", new MongoException("write failed")));
        var inner = new ScriptedExecuter(RuntimeMutation, failure, new ExecutionResult());

        var result = await NewExecutor(inner).ExecuteAsync(NewOptions());

        result.Should().BeSameAs(failure);
        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task UnknownTransactionCommitResult_IsNeverReExecuted()
    {
        // The commit may have succeeded on the server: running the handler again could write twice.
        var commit = Labelled(TransientTransactionRetry.UnknownTransactionCommitResultLabel);
        commit.AddErrorLabel(TransientTransactionRetry.TransientTransactionErrorLabel);
        var failure = Result(new ExecutionError("commit", commit));
        var inner = new ScriptedExecuter(RuntimeMutation, failure, new ExecutionResult());

        var result = await NewExecutor(inner).ExecuteAsync(NewOptions());

        result.Should().BeSameAs(failure);
        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RetryIsBounded_AndTheLastErrorSurfaces()
    {
        var results = Enumerable.Range(0, 10).Select(_ => TransientResult()).ToArray();
        var inner = new ScriptedExecuter(RuntimeMutation, results);
        var delays = new List<TimeSpan>();

        var result = await NewExecutor(inner, new TransientTransactionRetryOptions { MaxAttempts = 3 }, delays)
            .ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(3);
        result.Should().BeSameAs(results[2]);
        delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task DefaultOptions_AllowTenAttempts()
    {
        var inner = new ScriptedExecuter(RuntimeMutation, Enumerable.Range(0, 20).Select(_ => TransientResult()).ToArray());

        await NewExecutor(inner, new TransientTransactionRetryOptions()).ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(10);
    }

    [Fact]
    public async Task AttemptThatOutlastsTheTimeBudget_IsNotRetried()
    {
        // A transaction the server aborted because it ran too long also carries TransientTransactionError.
        var clock = new ManualClock();
        var failure = TransientResult();
        var inner = new ScriptedExecuter(RuntimeMutation, failure, new ExecutionResult())
        {
            DuringAttempt = _ => clock.Advance(TimeSpan.FromSeconds(61))
        };

        var result = await NewExecutor(inner, new TransientTransactionRetryOptions(), clock: clock)
            .ExecuteAsync(NewOptions());

        result.Should().BeSameAs(failure);
        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RetriesStop_WhenTheTimeBudgetIsUsedUp()
    {
        var clock = new ManualClock();
        var results = Enumerable.Range(0, 10).Select(_ => TransientResult()).ToArray();
        var inner = new ScriptedExecuter(RuntimeMutation, results)
        {
            DuringAttempt = _ => clock.Advance(TimeSpan.FromMilliseconds(400))
        };

        var result = await NewExecutor(inner,
                new TransientTransactionRetryOptions { MaxElapsed = TimeSpan.FromSeconds(1) }, clock: clock)
            .ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(3, "after the third attempt 1.2 s have passed");
        result.Should().BeSameAs(results[2]);
    }

    [Fact]
    public async Task DefaultTimeBudget_IsTenSeconds()
    {
        new TransientTransactionRetryOptions().MaxElapsed.Should().Be(TimeSpan.FromSeconds(10));

        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), new ExecutionResult());
        await NewExecutor(inner, new TransientTransactionRetryOptions { MaxElapsed = TimeSpan.Zero })
            .ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(1, "a budget of zero allows no retry");
    }

    [Fact]
    public async Task ProductDefaults_RetryABurstOfConflictsUpToTheAttemptBound_InsideTheTimeBudget()
    {
        // Nine conflicts of 10 ms each, then success: the backoff ceilings of the default options add up to at most
        // 4.26 s (20 ms doubling, capped at 1 s), so the 10 s budget never cuts off retries the attempt bound allows.
        var clock = new ManualClock();
        var results = Enumerable.Range(0, 9).Select(_ => TransientResult())
            .Append(new ExecutionResult { Executed = true }).ToArray();
        var inner = new ScriptedExecuter(RuntimeMutation, results)
        {
            DuringAttempt = _ => clock.Advance(TimeSpan.FromMilliseconds(10))
        };

        var result = await NewExecutor(inner, new TransientTransactionRetryOptions(), clock: clock)
            .ExecuteAsync(NewOptions());

        result.Should().BeSameAs(results[^1]);
        inner.Attempts.Should().Be(10);
        clock.GetElapsedTime(0).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DelaysBetweenAttempts_StayBelowTheDoublingCeilingAndTheMaximum()
    {
        var options = new TransientTransactionRetryOptions
        {
            MaxAttempts = 6, BaseDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(50)
        };
        var inner = new ScriptedExecuter(RuntimeMutation, Enumerable.Range(0, 6).Select(_ => TransientResult()).ToArray());
        var delays = new List<TimeSpan>();

        await NewExecutor(inner, options, delays).ExecuteAsync(NewOptions());

        delays.Should().HaveCount(5);
        double[] ceilings = [20, 40, 50, 50, 50];
        for (var i = 0; i < delays.Count; i++)
        {
            delays[i].Should().BeGreaterThanOrEqualTo(TimeSpan.Zero)
                .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(ceilings[i]), "retry {0}", i + 1);
        }
    }

    [Fact]
    public async Task CancelledRequest_IsNotRetried()
    {
        var failure = TransientResult();
        var inner = new ScriptedExecuter(RuntimeMutation, failure, new ExecutionResult());
        var options = NewOptions();
        options.CancellationToken = new CancellationToken(canceled: true);

        var result = await NewExecutor(inner).ExecuteAsync(options);

        result.Should().BeSameAs(failure);
        inner.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData("mutation { streamData { a } }")]
    [InlineData("mutation { blueprints { a } }")]
    [InlineData("mutation { constructionKit { a } }")]
    [InlineData("mutation { runtime { a } blueprints { a } }")]
    [InlineData("mutation { ...F } fragment F on Mutation { runtime { a } }")]
    public async Task MutationOutsideRuntime_IsNotRetried(string query)
    {
        var inner = new ScriptedExecuter(query, TransientResult(), new ExecutionResult());

        await NewExecutor(inner).ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RequestWithoutTransaction_IsNotRetried()
    {
        // Queries get no transaction from OctoSessionListener, so nothing marks the attempt.
        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), new ExecutionResult())
        {
            MarkTransaction = false
        };

        await NewExecutor(inner).ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RequestThatDispatchedAMethodCall_IsNotRetried()
    {
        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), new ExecutionResult())
        {
            DuringAttempt = TransientTransactionRetry.MarkNotRetryable
        };

        await NewExecutor(inner).ExecuteAsync(NewOptions());

        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task EveryAttempt_StartsFromTheOriginalListenersAndValidationRules()
    {
        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), TransientResult(), new ExecutionResult());
        var options = NewOptions();
        var ownListener = A.Fake<IDocumentExecutionListener>();
        options.Listeners.Add(ownListener);

        await NewExecutor(inner).ExecuteAsync(options);

        inner.Attempts.Should().Be(3);
        inner.ListenerCountsAtStart.Should().Equal(1, 1, 1);
        inner.ValidationRuleCountsAtStart.Should().Equal(0, 0, 0);
        options.Listeners.Should().HaveCount(2, "the last attempt keeps what the execution configuration added");
    }

    [Fact]
    public async Task StateOfTheAttempt_IsRemovedFromTheUserContextAfterwards()
    {
        var inner = new ScriptedExecuter(RuntimeMutation, TransientResult(), new ExecutionResult());
        var options = NewOptions();

        await NewExecutor(inner).ExecuteAsync(options);

        options.UserContext.Should().NotContainKey(TransientTransactionRetry.StateKey);
    }

    [Fact]
    public void BackoffDelay_StaysBelowTheDoublingCeilingAndTheMaximum()
    {
        var options = new TransientTransactionRetryOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(50)
        };

        for (var i = 0; i < 200; i++)
        {
            TransientTransactionRetry.BackoffDelay(options, 1).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(20));
            TransientTransactionRetry.BackoffDelay(options, 2).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(40));
            TransientTransactionRetry.BackoffDelay(options, 6).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(50));
            TransientTransactionRetry.BackoffDelay(options, 1).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }
    }

    private static MongoException Labelled(string label)
    {
        var exception = new MongoException("injected");
        exception.AddErrorLabel(label);
        return exception;
    }

    private static ExecutionResult Result(ExecutionError error) => new() { Errors = new ExecutionErrors { error } };

    private static ExecutionResult TransientResult() => Result(new ExecutionError("An error occurred",
        new InvalidOperationException("wrapped by the engine",
            Labelled(TransientTransactionRetry.TransientTransactionErrorLabel))));

    private static TenantDocumentExecutor NewExecutor(IDocumentExecuter inner,
        TransientTransactionRetryOptions? options = null, List<TimeSpan>? delays = null, TimeProvider? clock = null)
    {
        var schemaContext = A.Fake<ISchemaContext>();
        A.CallTo(() => schemaContext.GetOrCreateAsync(A<string>._)).Returns(Task.FromResult<ISchema>(new Schema()));
        return new TenantDocumentExecutor(schemaContext, inner, Options.Create(options ?? new TransientTransactionRetryOptions()),
            null, (delay, _) =>
            {
                delays?.Add(delay);
                // The clock moves with the backoff, as the real delay would.
                (clock as ManualClock)?.Advance(delay);
                return Task.CompletedTask;
            }, clock ?? new ManualClock());
    }

    /// <summary>A clock that moves only when the test advances it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static ExecutionOptions NewOptions() => new()
    {
        Query = RuntimeMutation,
        UserContext = new GraphQlUserContext(null, A.Fake<ITenantContext>())
    };

    private sealed class ScriptedExecuter(string query, params ExecutionResult[] results) : IDocumentExecuter
    {
        private readonly GraphQLOperationDefinition _operation =
            Parser.Parse(query).Definitions.OfType<GraphQLOperationDefinition>().First();

        public int Attempts { get; private set; }

        public bool MarkTransaction { get; init; } = true;

        public Action<IDictionary<string, object?>>? DuringAttempt { get; init; }

        public List<int> ListenerCountsAtStart { get; } = [];

        public List<int> ValidationRuleCountsAtStart { get; } = [];

        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            ListenerCountsAtStart.Add(options.Listeners.Count);
            ValidationRuleCountsAtStart.Add(options.ValidationRules?.Count() ?? 0);

            // What AddDocumentListener / AddValidationRule do on every execution.
            options.Listeners.Add(A.Fake<IDocumentExecutionListener>());
            options.ValidationRules = (options.ValidationRules ?? []).Append(A.Fake<IValidationRule>()).ToList();

            if (MarkTransaction)
            {
                TransientTransactionRetry.MarkTransactionStarted(options.UserContext, _operation);
            }

            DuringAttempt?.Invoke(options.UserContext);
            return Task.FromResult(results[Attempts++]);
        }
    }
}
