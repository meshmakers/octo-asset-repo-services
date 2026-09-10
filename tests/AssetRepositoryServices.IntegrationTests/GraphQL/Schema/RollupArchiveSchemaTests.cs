using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
/// AB#5157 — introspection contract of the multi-source / coverage GraphQL surface. The schema is
/// built for real here (the whole stream-data type set, including the two <c>BucketAlignment</c>
/// enums that must NOT collide), so a rename or an accidental non-null on the deprecated scalar
/// breaks a test instead of a studio release:
/// <list type="bullet">
/// <item><c>CreateRollupArchiveInput</c> keeps the optional deprecated <c>sourceArchiveRtId</c> and
/// gains the <c>sources</c> list of <c>CreateRollupSourceInput</c>.</item>
/// <item><c>RollupArchiveInfo.sourceArchiveRtId</c> is nullable AND deprecated in favour of the new
/// non-null <c>sources</c> list.</item>
/// <item>The input enum (<c>BucketAlignmentInput</c>) and the new output enum
/// (<c>BucketAlignment</c>) both carry <c>CALENDAR_QUARTER</c>.</item>
/// <item><c>SeriesResolutionSignal</c> carries <c>COVERAGE_LIMITED</c> and
/// <c>ResolveSeriesQueryResult</c> carries <c>finerRungAvailableFrom</c>.</item>
/// <item><c>ArchiveCoverageInfo</c> exists with its rung projection.</item>
/// </list>
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupArchiveSchemaTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task CreateRollupArchiveInput_KeepsTheOptionalScalar_AndDeclaresTheSourcesList()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""CreateRollupArchiveInput"") {
                kind
                inputFields {
                  name
                  type { kind name ofType { kind name ofType { kind name } } }
                }
              }
            }");

        type["kind"]!.Value<string>().Should().Be("INPUT_OBJECT");
        var inputFields = (JArray)type["inputFields"]!;

        var scalar = FieldNamed(inputFields, "sourceArchiveRtId");
        scalar["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "the deprecated single-source shorthand must stay optional — a NON_NULL wrapper would break every multi-source caller");
        scalar["type"]!["name"]!.Value<string>().Should().Be("OctoObjectId");

        var sources = FieldNamed(inputFields, "sources");
        sources["type"]!["kind"]!.Value<string>().Should().Be("LIST",
            "the list itself is nullable so a caller may send only the deprecated scalar");
        sources["type"]!["ofType"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        sources["type"]!["ofType"]!["ofType"]!["name"]!.Value<string>().Should().Be("CreateRollupSourceInput");
    }

    [Fact]
    public async Task CreateRollupSourceInput_CarriesTheArchiveIdAndTheHalfOpenSpan()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""CreateRollupSourceInput"") {
                inputFields { name type { kind name ofType { kind name } } }
              }
            }");

        var inputFields = (JArray)type["inputFields"]!;
        inputFields.Select(f => f["name"]!.Value<string>())
            .Should().BeEquivalentTo("sourceArchiveRtId", "validFrom", "validTo");

        var archiveRtId = FieldNamed(inputFields, "sourceArchiveRtId");
        archiveRtId["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL", "a source entry without an archive is meaningless");
        archiveRtId["type"]!["ofType"]!["name"]!.Value<string>().Should().Be("OctoObjectId");

        FieldNamed(inputFields, "validFrom")["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "an open start is expressed by omitting validFrom");
        FieldNamed(inputFields, "validTo")["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "an open end is expressed by omitting validTo");
    }

    [Fact]
    public async Task RollupArchiveInfo_DeprecatesTheScalar_AndExposesTheSourcesList()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""RollupArchiveInfo"") {
                fields(includeDeprecated: true) {
                  name
                  isDeprecated
                  deprecationReason
                  type { kind name ofType { kind name ofType { kind name } } }
                }
              }
            }");

        var fields = (JArray)type["fields"]!;

        var scalar = FieldNamed(fields, "sourceArchiveRtId");
        scalar["isDeprecated"]!.Value<bool>().Should().BeTrue();
        scalar["deprecationReason"]!.Value<string>().Should().Contain("sources");
        scalar["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "SingleUnboundedSourceRtId is null for a multi-source rollup, so the field must be nullable");

        var sources = FieldNamed(fields, "sources");
        sources["isDeprecated"]!.Value<bool>().Should().BeFalse();
        sources["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        sources["type"]!["ofType"]!["kind"]!.Value<string>().Should().Be("LIST");
        sources["type"]!["ofType"]!["ofType"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
    }

    [Fact]
    public async Task RollupSourceInfo_ProjectsTheHalfOpenSpan()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""RollupSourceInfo"") {
                fields { name type { kind name ofType { kind name } } }
              }
            }");

        var fields = (JArray)type["fields"]!;
        fields.Select(f => f["name"]!.Value<string>())
            .Should().BeEquivalentTo("sourceArchiveRtId", "validFrom", "validTo");
        FieldNamed(fields, "sourceArchiveRtId")["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
    }

    [Fact]
    public async Task BothAlignmentEnums_CarryCalendarQuarter_UnderTheirOwnNames()
    {
        // AB#5157 added CalendarQuarter (=5) AND a second, output-side enum. GraphQL forbids one
        // enum name in both positions, so the input keeps 'BucketAlignmentInput' while the coverage
        // projection introduces 'BucketAlignment'. Both must list the full ladder.
        fixture.OutputHelper = output;
        var expected = new[]
        {
            "FIXED_SIZE", "CALENDAR_DAY", "ISO_8601_WEEK", "CALENDAR_MONTH", "CALENDAR_QUARTER", "CALENDAR_YEAR"
        };

        var input = await IntrospectTypeAsync(@"
            query { __type(name: ""BucketAlignmentInput"") { kind enumValues { name } } }");
        input["kind"]!.Value<string>().Should().Be("ENUM");
        EnumValues(input).Should().BeEquivalentTo(expected);

        var outputEnum = await IntrospectTypeAsync(@"
            query { __type(name: ""BucketAlignment"") { kind enumValues { name } } }");
        outputEnum["kind"]!.Value<string>().Should().Be("ENUM");
        EnumValues(outputEnum).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SeriesResolutionSignal_CarriesCoverageLimited()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query { __type(name: ""SeriesResolutionSignal"") { enumValues { name } } }");

        EnumValues(type).Should().Contain("COVERAGE_LIMITED",
            "the resolver emits it when the coverage filter redirects to a covering rung");
    }

    [Fact]
    public async Task ResolveSeriesQueryResult_CarriesFinerRungAvailableFrom()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""ResolveSeriesQueryResult"") {
                fields { name type { kind name } }
              }
            }");

        var fields = (JArray)type["fields"]!;
        var finer = FieldNamed(fields, "finerRungAvailableFrom");
        finer["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "null means 'no finer rung would ever have data for this window'");
        finer["type"]!["name"]!.Value<string>().Should().Be("DateTime");
    }

    [Fact]
    public async Task ArchiveCoverageInfo_ProjectsTheRung()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""ArchiveCoverageInfo"") {
                fields { name type { kind name ofType { kind name ofType { kind name } } } }
              }
            }");

        var fields = (JArray)type["fields"]!;
        fields.Select(f => f["name"]!.Value<string>()).Should().BeEquivalentTo(
            "archiveRtId", "rtWellKnownName", "isBase", "status", "bucketSizeMs",
            "bucketAlignment", "storedFunctions", "availableFrom", "availableTo");

        var status = FieldNamed(fields, "status");
        status["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        status["type"]!["ofType"]!["name"]!.Value<string>().Should().Be("String",
            "the rung reports the CkArchiveStatus name, not an enum type of its own");

        FieldNamed(fields, "bucketAlignment")["type"]!["ofType"]!["name"]!.Value<string>()
            .Should().Be("BucketAlignment");
        FieldNamed(fields, "bucketSizeMs")["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "a base archive has no bucket grain");
        FieldNamed(fields, "availableFrom")["type"]!["kind"]!.Value<string>().Should().Be("SCALAR",
            "an archive without data reports null coverage");
    }

    [Fact]
    public async Task StreamDataRoot_ExposesRollupsForAndCoverageFor()
    {
        fixture.OutputHelper = output;

        var type = await IntrospectTypeAsync(@"
            query {
              __type(name: ""StreamDataModelQuery"") {
                fields { name type { kind ofType { kind name ofType { kind name } } } }
              }
            }");

        var fields = (JArray)type["fields"]!;
        var names = fields.Select(f => f["name"]!.Value<string>()).ToList();
        names.Should().Contain("rollupsFor").And.Contain("coverageFor");

        var coverage = FieldNamed(fields, "coverageFor");
        coverage["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        coverage["type"]!["ofType"]!["kind"]!.Value<string>().Should().Be("LIST");
        coverage["type"]!["ofType"]!["ofType"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static JToken FieldNamed(JArray fields, string name)
    {
        var field = fields.FirstOrDefault(f => f["name"]!.Value<string>() == name);
        field.Should().NotBeNull($"the schema must expose '{name}'");
        return field!;
    }

    private static IEnumerable<string?> EnumValues(JToken type) =>
        ((JArray)type["enumValues"]!).Select(v => v["name"]!.Value<string>());

    private async Task<JToken> IntrospectTypeAsync(string query)
    {
        var result = await fixture.ExecuteGraphQlAsync(query);
        result.Errors.Should().BeNullOrEmpty();

        var json = fixture.SerializeGraphQl(result);
        fixture.OutputHelper?.WriteLine(json);
        var type = JObject.Parse(json).SelectToken("data.__type");
        type.Should().NotBeNull("the queried type must exist in the schema");
        return type!;
    }
}
