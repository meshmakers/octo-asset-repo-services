using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#7020: the rule "generic mutations or exactly one CK method call per request" is registered in the real
///     GraphQL pipeline and judges the real, generated schema (marker on the generated method fields). The unit tests
///     of the rule cover aliases, fragments, operations and the byte-identical refusal.
/// </summary>
[Collection(GraphQlCollection.Name)]
public class OneMethodCallPerRequestTests
{
    private const string UnknownRtId = "5f9b3b3b3b3b3b3b3b3b3b3b";

    private readonly GraphQlTestFixture _fixture;

    public OneMethodCallPerRequestTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task MethodNextToGenericMutation_IsRefusedWithOneError_BeforeAnythingRuns()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation ($entities: [RtEntityId!]!) {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccounts {
                  touch(rtId: "{{UnknownRtId}}") { invocationId }
                }
                runtimeEntities { delete(entities: $entities) }
              }
            }
            """, """{ "entities": [ { "rtId": "5f9b3b3b3b3b3b3b3b3b3b3b", "ckTypeId": "AssetRepositoryIntegrationTest/AccessTestAccount" } ] }""");

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().HaveCount(1, json);
        var error = result.Errors![0];
        error.Code.Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        error.Extensions!["code"].Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        result.Data.Should().BeNull("a refused request runs nothing, not even the navigation resolvers");
    }

    [Fact]
    public async Task TwoMethodCalls_AreRefusedWithOneError()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccounts {
                  a: touch(rtId: "{{UnknownRtId}}") { invocationId }
                  b: lookup { invocationId }
                }
              }
            }
            """);

        result.Errors.Should().HaveCount(1, _fixture.SerializeGraphQl(result));
        result.Errors![0].Code.Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task OneMethodCall_PassesTheRule()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccounts {
                  touch(rtId: "{{UnknownRtId}}") { invocationId }
                }
              }
            }
            """);

        // The resolver answers (METHOD_* until the gateway is wired, AB#7026), never our code.
        result.Errors.Should().NotBeNull();
        result.Errors!.Select(e => e.Code).Should().NotContain("ONE_METHOD_CALL_PER_REQUEST");
    }
}
