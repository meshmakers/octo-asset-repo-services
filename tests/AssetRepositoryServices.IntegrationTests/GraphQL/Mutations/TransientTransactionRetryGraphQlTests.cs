using System.Text.Json;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
///     Against a real MongoDB replica set: a GraphQL mutation whose transaction fails with
///     <c>TransientTransactionError</c> is executed again as a whole (new transaction, handler again). Parallel creations
///     of an auto-numbered type in separate requests collide on the counter (one <c>$inc</c> per counter inside
///     the request transaction, a parallel writer aborts it) and must all succeed with distinct, gap-free numbers.
/// </summary>
[Collection(AutoIncrementGraphQlCollection.Name)]
public class TransientTransactionRetryGraphQlTests(AutoIncrementGraphQlTestFixture fixture)
{
    private const string CreateMutation = """
        mutation ($entities: [RtEntityInput!]!) {
          runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }
        """;

    private const int ParallelRequests = 10;

    [Fact]
    public async Task ParallelCreations_InSeparateRequests_AllSucceedWithDistinctGapFreeNumbers()
    {
        var prefix = $"parallel-{Guid.NewGuid():N}-";

        var results = await Task.WhenAll(Enumerable.Range(0, ParallelRequests)
            .Select(i => Task.Run(() => fixture.ExecuteGraphQlAsync(CreateMutation, Variables(prefix + i)))));

        foreach (var result in results)
        {
            result.Errors.Should().BeNullOrEmpty("{0}, MongoDB labels: {1}", fixture.SerializeGraphQl(result),
                string.Join(",", (result.Errors ?? []).Select(e => e.InnerException).OfType<MongoException>()
                    .SelectMany(e => e.ErrorLabels)));
        }

        var numbers = (await fixture.NumbersAsync(prefix)).Order().ToList();
        numbers.Should().HaveCount(ParallelRequests);
        numbers.Should().OnlyHaveUniqueItems();
        (numbers[^1] - numbers[0]).Should().Be(ParallelRequests - 1, "the numbers of rolled-back attempts are reused");
    }

    [Fact]
    public async Task InjectedTransientError_IsRetriedOnce_AndTheEntityAndItsNumberExistOnce()
    {
        var before = await NextNumberProbeAsync();
        var title = $"injected-{Guid.NewGuid():N}";
        var injector = new ErrorInjectingListener(_ => TransientException());

        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title),
            configure: options => options.Listeners.Add(injector));

        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        injector.Attempts.Should().Be(2);
        injector.OctoSessionListenersPerAttempt.Should().Equal(1, 1);
        var numbers = await fixture.NumbersAsync(title);
        numbers.Should().ContainSingle("the first attempt was rolled back")
            .Which.Should().Be(before + 1, "the number of the rolled-back attempt is reused");
    }

    [Fact]
    public async Task InjectedErrorWithoutTheLabel_IsNotRetried_AndSurfaces()
    {
        var title = $"not-transient-{Guid.NewGuid():N}";
        var injector = new ErrorInjectingListener(_ => new MongoException("not transient"));

        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title),
            configure: options => options.Listeners.Add(injector));

        injector.Attempts.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Message.Should().Be("injected");
        (await fixture.NumbersAsync(title)).Should().BeEmpty("the transaction was aborted");
    }

    [Fact]
    public async Task TransientErrorOnEveryAttempt_StopsAtTheBound_AndSurfaces()
    {
        var title = $"always-{Guid.NewGuid():N}";
        var injector = new ErrorInjectingListener(_ => TransientException(), failAttempts: int.MaxValue);

        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title),
            configure: options => options.Listeners.Add(injector));

        injector.Attempts.Should().Be(new TransientTransactionRetryOptions().MaxAttempts);
        result.Errors.Should().ContainSingle().Which.Message.Should().Be("injected");
        (await fixture.NumbersAsync(title)).Should().BeEmpty();
    }

    /// <summary>Creates one probe ticket and returns its number: the counter's current value.</summary>
    private async Task<long> NextNumberProbeAsync()
    {
        var title = $"probe-{Guid.NewGuid():N}";
        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title));
        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        return (await fixture.NumbersAsync(title)).Single();
    }

    private static MongoException TransientException()
    {
        var exception = new MongoException("injected write conflict");
        exception.AddErrorLabel(TransientTransactionRetry.TransientTransactionErrorLabel);
        return exception;
    }

    private static string Variables(string title) => JsonSerializer.Serialize(new
    {
        entities = new[]
        {
            new
            {
                ckTypeId = AutoIncrementGraphQlTestFixture.TicketCkTypeId,
                attributes = new object[] { new { attributeName = "title", value = title } }
            }
        }
    });

    /// <summary>
    ///     Added to the options before execution, so it runs before <see cref="OctoSessionListener" />: its
    ///     <c>AfterExecutionAsync</c> adds an error to the first <paramref name="failAttempts" /> attempts after the
    ///     resolvers wrote, and <see cref="OctoSessionListener" /> then aborts the transaction.
    /// </summary>
    private sealed class ErrorInjectingListener(Func<int, Exception> error, int failAttempts = 1)
        : IDocumentExecutionListener
    {
        public int Attempts { get; private set; }

        public List<int> OctoSessionListenersPerAttempt { get; } = [];

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
}
