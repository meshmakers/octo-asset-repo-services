using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Errors;

/// <summary>
///     F1.5-S4 (AB#5923, CK v2 E2E finding R2-4): outside Development, GraphQL errors carry code and message
///     but no exception details (stack traces, local source paths). Access errors carry code, attribute path and
///     operation, never a value, in every environment.
/// </summary>
internal static class GraphQlErrorDetailsAssertions
{
    public const string InvalidObjectIdQuery = """
        query ($id: OctoObjectId!) {
          runtime { runtimeEntities(ckId: "AssetRepositoryIntegrationTest/Customer", rtId: $id) { totalCount } }
        }
        """;

    public const string InvalidObjectIdVariables = """{ "id": "not-an-object-id" }""";

    public const string HiddenFilterQuery = """
        query {
          runtime {
            assetRepositoryIntegrationTestAccessTestAccount(
              fieldFilter: [{ attributePath: "passwordHash", operator: EQUALS, comparisonValue: "GUESSED-VALUE-42" }]) {
              totalCount
            }
          }
        }
        """;

    public const string HiddenSelectorQuery = """
        query {
          runtime { transientQuery {
            simple(ckId: "AssetRepositoryIntegrationTest/AccessTestGroup",
                   columnPaths: ["name", "members.assetRepositoryIntegrationTestAccessTestAccount[passwordHash='GUESSED-VALUE-42']->name"]) {
              items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } }
            }
          } }
        }
        """;

    public static JArray Errors(GraphQlTestFixture fixture, ExecutionResult result)
    {
        result.Errors.Should().NotBeNullOrEmpty();
        return (JArray)JObject.Parse(fixture.SerializeGraphQl(result))["errors"]!;
    }

    public static void AssertNoDiagnostics(JArray errors)
    {
        foreach (var error in errors)
        {
            error["message"]!.Value<string>().Should().NotBeNullOrWhiteSpace();
            error.SelectToken("extensions.code")!.Value<string>().Should().NotBeNullOrWhiteSpace();
            error.SelectToken("extensions.details").Should().BeNull("exception details are Development-only");

            var raw = error.ToString(Newtonsoft.Json.Formatting.None);
            raw.Should().NotContain(".cs:line").And.NotContain("/Users/").And.NotContain("   at ");
        }
    }

    public static void AssertAccessErrorWithoutValue(JArray errors)
    {
        var error = errors.Single(e => e.SelectToken("extensions.code")!.Value<string>() == "ATTRIBUTE_NOT_QUERYABLE");
        error.SelectToken("extensions.attributePath")!.Value<string>().Should().Contain("passwordHash");
        error.SelectToken("extensions.operation")!.Value<string>().Should().NotBeNullOrWhiteSpace();
        errors.ToString(Newtonsoft.Json.Formatting.None).Should().NotContain("GUESSED-VALUE-42");
    }
}

[Collection(GraphQlProductionCollection.Name)]
public class GraphQlErrorDetailsProductionTests(ProductionGraphQlTestFixture fixture, ITestOutputHelper output)
{
    private readonly ProductionGraphQlTestFixture _fixture = Init(fixture, output);

    [Fact]
    public async Task InvalidScalarVariable_HasCodeAndMessage_ButNoDetails()
    {
        var result = await _fixture.ExecuteGraphQlAsync(GraphQlErrorDetailsAssertions.InvalidObjectIdQuery,
            GraphQlErrorDetailsAssertions.InvalidObjectIdVariables);

        GraphQlErrorDetailsAssertions.AssertNoDiagnostics(GraphQlErrorDetailsAssertions.Errors(_fixture, result));
    }

    [Theory]
    [InlineData(GraphQlErrorDetailsAssertions.HiddenFilterQuery)]
    [InlineData(GraphQlErrorDetailsAssertions.HiddenSelectorQuery)]
    public async Task AccessError_HasCodePathAndOperation_ButNoValueAndNoDetails(string query)
    {
        var errors = GraphQlErrorDetailsAssertions.Errors(_fixture, await _fixture.ExecuteGraphQlAsync(query));

        GraphQlErrorDetailsAssertions.AssertNoDiagnostics(errors);
        GraphQlErrorDetailsAssertions.AssertAccessErrorWithoutValue(errors);
    }

    private static ProductionGraphQlTestFixture Init(ProductionGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        fixture.OutputHelper = output;
        return fixture;
    }
}

[Collection(GraphQlDevelopmentCollection.Name)]
public class GraphQlErrorDetailsDevelopmentTests(DevelopmentGraphQlTestFixture fixture, ITestOutputHelper output)
{
    private readonly DevelopmentGraphQlTestFixture _fixture = Init(fixture, output);

    [Fact]
    public async Task InvalidScalarVariable_KeepsTheDiagnostics()
    {
        // Development keeps today's diagnostics (exception details with stack trace).
        var result = await _fixture.ExecuteGraphQlAsync(GraphQlErrorDetailsAssertions.InvalidObjectIdQuery,
            GraphQlErrorDetailsAssertions.InvalidObjectIdVariables);

        var errors = GraphQlErrorDetailsAssertions.Errors(_fixture, result);
        errors.Should().Contain(e => e.SelectToken("extensions.details") != null);
    }

    [Theory]
    [InlineData(GraphQlErrorDetailsAssertions.HiddenFilterQuery)]
    [InlineData(GraphQlErrorDetailsAssertions.HiddenSelectorQuery)]
    public async Task AccessError_HasNoValue_EvenWithDiagnostics(string query)
    {
        var errors = GraphQlErrorDetailsAssertions.Errors(_fixture, await _fixture.ExecuteGraphQlAsync(query));

        GraphQlErrorDetailsAssertions.AssertAccessErrorWithoutValue(errors);
    }

    private static DevelopmentGraphQlTestFixture Init(DevelopmentGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        fixture.OutputHelper = output;
        return fixture;
    }
}
