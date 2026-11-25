using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
///     Wiring of the transient transaction retry against a real MongoDB replica set: a GraphQL mutation whose
///     transaction fails with <c>TransientTransactionError</c> is executed again as a whole (new transaction, handler
///     again) and its effects exist once. The retry policy itself (bound, time budget, backoff, which errors and
///     requests qualify) is covered by the unit tests <c>TransientTransactionRetryTests</c>; the host runs with the
///     product's retry options.
/// </summary>
[Collection(AutoIncrementGraphQlCollection.Name)]
public class TransientTransactionRetryGraphQlTests(AutoIncrementGraphQlTestFixture fixture)
{
    private const string CreateMutation = """
        mutation ($entities: [RtEntityInput!]!) {
          runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }
        """;

    /// <summary>
    ///     The first attempt writes the ticket, takes a number and uploads the bytes of its attachment to GridFS, then
    ///     fails with an injected transient error. The second attempt commits. The ticket and its number exist once,
    ///     the number of the rolled-back attempt is reused, and the upload of the rolled-back attempt is deleted again
    ///     (rollback action of the session), so the bytes exist once.
    /// </summary>
    [Fact]
    public async Task InjectedTransientError_IsRetried_AndTheEntityItsNumberAndItsBytesExistOnce()
    {
        var before = await NextNumberProbeAsync();
        var filesBefore = await fixture.CountGridFsFilesAsync();
        var title = $"injected-{Guid.NewGuid():N}";
        var injector = new TransientErrorInjectingListener(_ => TransientErrorInjectingListener.TransientException());

        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title, attachment: [1, 2, 3]),
            configure: options => options.Listeners.Add(injector));

        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        injector.Attempts.Should().Be(2);
        injector.OctoSessionListenersPerAttempt.Should().Equal(1, 1);
        var numbers = await fixture.NumbersAsync(title);
        numbers.Should().ContainSingle("the first attempt was rolled back")
            .Which.Should().Be(before + 1, "the number of the rolled-back attempt is reused");
        (await fixture.CountGridFsFilesAsync()).Should().Be(filesBefore + 1,
            "the upload of the rolled-back attempt is deleted, the committed attempt keeps its own");
    }

    /// <summary>
    ///     Parallel creations of an auto-numbered type in separate requests collide on the counter (one <c>$inc</c>
    ///     per counter inside the request transaction, a parallel writer aborts it) and must all succeed with
    ///     distinct, gap-free numbers. Contention depends on the machine's timing, so the test runs in the
    ///     performance pipeline, not in the regular suite.
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Category", "LoadSensitive")]
    public async Task ParallelCreations_InSeparateRequests_AllSucceedWithDistinctGapFreeNumbers()
    {
        const int parallelRequests = 10;
        var prefix = $"parallel-{Guid.NewGuid():N}-";

        var results = await Task.WhenAll(Enumerable.Range(0, parallelRequests)
            .Select(i => Task.Run(() => fixture.ExecuteGraphQlAsync(CreateMutation, Variables(prefix + i)))));

        foreach (var result in results)
        {
            result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        }

        var numbers = (await fixture.NumbersAsync(prefix)).Order().ToList();
        numbers.Should().HaveCount(parallelRequests);
        numbers.Should().OnlyHaveUniqueItems();
        (numbers[^1] - numbers[0]).Should().Be(parallelRequests - 1, "the numbers of rolled-back attempts are reused");
    }

    /// <summary>Creates one probe ticket and returns its number: the counter's current value.</summary>
    private async Task<long> NextNumberProbeAsync()
    {
        var title = $"probe-{Guid.NewGuid():N}";
        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title));
        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        return (await fixture.NumbersAsync(title)).Single();
    }

    private static string Variables(string title, byte[]? attachment = null)
    {
        var attributes = new List<object> { new { attributeName = "title", value = (object)title } };
        if (attachment != null)
        {
            attributes.Add(new { attributeName = "attachment", value = (object)attachment });
        }

        return JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new { ckTypeId = AutoIncrementGraphQlTestFixture.TicketCkTypeId, attributes }
            }
        });
    }
}
