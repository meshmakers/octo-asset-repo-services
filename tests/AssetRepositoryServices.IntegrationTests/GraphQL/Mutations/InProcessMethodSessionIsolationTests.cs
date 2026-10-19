using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

[CollectionDefinition(Name)]
public class InProcessMethodCollection : ICollectionFixture<InProcessMethodGraphQlTestFixture>
{
    public const string Name = "InProcessMethod";
}

/// <summary>
///     AB#7023 (OD-8 part 2): a CK method started in-process from inside a real GraphQL mutation (real
///     <c>OctoSessionListener</c> session and MongoDB transaction) never sees that session, cannot end it, and leaves the
///     mutation's own commit intact.
/// </summary>
[Collection(InProcessMethodCollection.Name)]
public class InProcessMethodSessionIsolationTests
{
    private const string Mutation = @"
        mutation ($entities: [RtEntityInput!]!) {
            runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }";

    private readonly InProcessMethodGraphQlTestFixture _fixture;

    public InProcessMethodSessionIsolationTests(InProcessMethodGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Observation.HasSessionInResolver = null;
        _fixture.Observation.HasSessionInResolverAfterMethod = null;
        _fixture.Observation.HasSessionInHandler = null;
        _fixture.Observation.HandlerRuns = 0;
    }

    private static string CreateVariables(string wellKnownName) => JsonSerializer.Serialize(new
    {
        entities = new[]
        {
            new
            {
                ckTypeId = "AssetRepositoryIntegrationTest/Customer",
                rtWellKnownName = wellKnownName,
                attributes = new[]
                {
                    new { attributeName = "firstName", value = "Ada" },
                    new { attributeName = "lastName", value = "Lovelace" },
                    new { attributeName = "street", value = "Test Street 7" },
                    new { attributeName = "postalCode", value = "12345" },
                    new { attributeName = "city", value = "Test City" },
                    new { attributeName = "country", value = "Austria" }
                }
            }
        }
    });

    private async Task<bool> CustomerExistsAsync(string rtId)
    {
        var result = await _fixture.ExecuteGraphQlAsync(@"query { runtime { assetRepositoryIntegrationTestCustomer(first: 1000, after: null) { items { rtId } } } }", "{}");
        result.Errors.Should().BeNullOrEmpty();
        var items = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        return items!.Any(i => i["rtId"]?.Value<string>() == rtId);
    }

    private async Task<string> RunAsync(string note, string wellKnownName)
    {
        var (result, response) = await _fixture.ExecuteAndStartMethodInResolverAsync(Mutation,
            CreateVariables(wellKnownName), note);

        result.Errors.Should().BeNullOrEmpty();
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(response.Error?.Code);
        var rtId = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
        return rtId;
    }

    [Fact]
    public async Task HandlerStartedFromAMutation_HasNoSession_AndTheMutationStillCommits()
    {
        var rtId = await RunAsync("touch", "InProcessSession_Plain");

        _fixture.Observation.HasSessionInResolver.Should().BeTrue("the resolver runs inside the request session");
        _fixture.Observation.HandlerRuns.Should().Be(1);
        _fixture.Observation.HasSessionInHandler.Should().BeFalse();
        _fixture.Observation.HasSessionInResolverAfterMethod.Should().BeTrue("the request session is still active");
        (await CustomerExistsAsync(rtId)).Should().BeTrue("the mutation committed after the method returned");
    }

    [Fact]
    public async Task HandlerThatCommitsAndAborts_DoesNotAffectTheRequestTransaction()
    {
        var rtId = await RunAsync("end-session", "InProcessSession_EndSession");

        _fixture.Observation.HasSessionInHandler.Should().BeFalse();
        _fixture.Observation.HasSessionInResolverAfterMethod.Should().BeTrue();
        (await CustomerExistsAsync(rtId)).Should().BeTrue("an abort in the handler must not roll the mutation back");
    }
}
