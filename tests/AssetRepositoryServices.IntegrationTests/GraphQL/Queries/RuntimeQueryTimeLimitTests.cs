using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     The server-side time limit of runtime-entity queries in the asset repository host: registered with the default of
///     120 s, read per query, and runtime queries answer normally under it. That MongoDB stops a query above the limit is
///     proven against MongoDB in the engine; the mapping of the time-out to ASSET1008 / 504 by the unit tests of
///     <c>RuntimeQueryTimeoutError</c>.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public sealed class RuntimeQueryTimeLimitTests(GraphQlTestFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void TheLimit_IsRegisteredWithTheDefaultOf120Seconds()
    {
        var options = fixture.Provider!.GetRequiredService<IOptions<RuntimeQueryOptions>>().Value;

        options.MaxTimeMs.Should().Be(120_000);
        RuntimeQueryOptions.SectionName.Should().Be("RuntimeQuery");
    }

    [Fact]
    public async Task RuntimeQueries_AnswerNormallyUnderTheLimit()
    {
        fixture.OutputHelper = output;

        var paged = await fixture.ExecuteGraphQlAsync(
            "{ runtime { assetRepositoryIntegrationTestCustomer(first: 2) { totalCount items { rtId } } } }");
        var count = await fixture.ExecuteGraphQlAsync(
            "{ runtime { assetRepositoryIntegrationTestCustomer { totalCount } } }");

        output.WriteLine(fixture.SerializeGraphQl(paged));
        paged.Errors.Should().BeNullOrEmpty();
        count.Errors.Should().BeNullOrEmpty();
        fixture.SerializeGraphQl(paged).Should().Contain("\"rtId\"");
    }
}
