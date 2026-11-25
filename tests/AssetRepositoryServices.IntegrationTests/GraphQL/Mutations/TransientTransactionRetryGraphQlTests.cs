using System.Text.Json;
using FluentAssertions;
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

    private const string ProductCreateMutation = """
        mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
          runtime { assetRepositoryIntegrationTestProducts { create(entities: $entities) { rtId } } }
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
        var injector = new TransientErrorInjectingListener(_ => TransientErrorInjectingListener.TransientException());

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
        var injector = new TransientErrorInjectingListener(_ => new MongoException("not transient"));

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
        var injector = new TransientErrorInjectingListener(_ => TransientErrorInjectingListener.TransientException(),
            failAttempts: int.MaxValue);

        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title),
            configure: options => options.Listeners.Add(injector));

        injector.Attempts.Should().Be(AutoIncrementGraphQlTestFixture.RetryMaxAttempts);
        result.Errors.Should().ContainSingle().Which.Message.Should().Be("injected");
        (await fixture.NumbersAsync(title)).Should().BeEmpty();
    }

    /// <summary>
    ///     A side effect outside the transaction happens once: the bytes of a binary attribute are uploaded to GridFS
    ///     by every attempt, and the rolled-back attempt deletes its upload again (rollback action of the session).
    /// </summary>
    [Fact]
    public async Task InjectedTransientError_InACreateWithABinary_LeavesTheUploadedBytesOnce()
    {
        var name = $"binary-{Guid.NewGuid():N}";
        var filesBefore = await fixture.CountGridFsFilesAsync();
        var injector = new TransientErrorInjectingListener(_ => TransientErrorInjectingListener.TransientException());

        var result = await fixture.ExecuteGraphQlAsync(ProductCreateMutation, JsonSerializer.Serialize(new
        {
            entities = new[] { new { productName = name, productCode = name, productImage = new byte[] { 1, 2, 3 } } }
        }), configure: options => options.Listeners.Add(injector));

        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        injector.Attempts.Should().Be(2);
        (await fixture.CountGridFsFilesAsync()).Should().Be(filesBefore + 1,
            "the upload of the rolled-back attempt is deleted, the committed attempt keeps its own");
    }

    /// <summary>Creates one probe ticket and returns its number: the counter's current value.</summary>
    private async Task<long> NextNumberProbeAsync()
    {
        var title = $"probe-{Guid.NewGuid():N}";
        var result = await fixture.ExecuteGraphQlAsync(CreateMutation, Variables(title));
        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
        return (await fixture.NumbersAsync(title)).Single();
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
}
