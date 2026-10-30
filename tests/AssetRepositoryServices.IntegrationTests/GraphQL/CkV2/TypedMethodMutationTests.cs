using System.Text.RegularExpressions;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#5670 (F3.7): one typed mutation per CK method under the per-type mutation field, with an input type from
///     the declared parameters and a result type from the declared result. Test types: <c>AccessTestAccount</c>
///     (instance methods, a static method, two versions of one method), <c>OperableThing</c> /
///     <c>OperableThingChild</c> (own methods and a method inherited from the interface <c>Operable-1</c>),
///     <c>PlainThing</c> (no methods) and <c>MethodLoadThing</c> (50 methods).
/// </summary>
[Collection(GraphQlCollection.Name)]
public class TypedMethodMutationTests
{
    private const string Prefix = "AssetRepositoryIntegrationTest";
    
    private readonly GraphQlTestFixture _fixture;

    public TypedMethodMutationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task MethodsOfAType_AreMutationFieldsNextToCreateAndUpdate()
    {
        var fields = await FieldsAsync($"{Prefix}AccessTestAccountMutations");

        fields.Keys.Should().BeEquivalentTo("create", "update", "resetPassword", "resetPassword2", "issueToken", "touch", "lookup");
    }

    [Fact]
    public async Task InstanceMethod_TakesTheTargetRtId_AndStaticMethodDoesNot()
    {
        var fields = await FieldsAsync($"{Prefix}AccessTestAccountMutations");

        ArgumentNames(fields["resetPassword"]).Should().Equal("rtId", "input", "idempotencyKey");
        ArgumentNames(fields["touch"]).Should().Equal("rtId", "idempotencyKey");
        ArgumentNames(fields["lookup"]).Should().Equal(["idempotencyKey"], "a static method has no target and no parameters");
        var rtId = fields["touch"]["args"]!.Single(a => a["name"]!.Value<string>() == "rtId");
        rtId["type"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        rtId["type"]!["ofType"]!["name"]!.Value<string>().Should().Be("OctoObjectId");
    }

    [Fact]
    public async Task EveryMethodField_HasTheOptionalStringArgumentIdempotencyKey()
    {
        // AB#7825: on every typed method mutation of the test model, instance and static, with and without input
        var methodFields = new[] { "resetPassword", "resetPassword2", "issueToken", "touch", "lookup" };
        var fields = await FieldsAsync($"{Prefix}AccessTestAccountMutations");
        var checkedFields = 0;
        foreach (var name in methodFields)
        {
            var argument = fields[name]["args"]!.Single(a => a["name"]!.Value<string>() == "idempotencyKey");
            argument["type"]!["kind"]!.Value<string>().Should().Be("SCALAR", $"{name}: the argument is optional");
            argument["type"]!["name"]!.Value<string>().Should().Be("String", name);
            checkedFields++;
        }

        // the operable thing type of the test model (inherited and interface methods)
        var operable = await FieldsAsync($"{Prefix}OperableThingMutations");
        foreach (var field in operable.Where(f => f.Key is not ("create" or "update")))
        {
            field.Value["args"]!.Select(a => a["name"]!.Value<string>()).Should().Contain("idempotencyKey", field.Key);
            checkedFields++;
        }

        checkedFields.Should().BeGreaterThan(methodFields.Length);
    }

    [Fact]
    public async Task Input_IsBuiltFromTheDeclaredParameters_RequiredAreNonNull()
    {
        var input = await TypeAsync($"{Prefix}AccessTestAccountResetPasswordMethodInput");
        input["kind"]!.Value<string>().Should().Be("INPUT_OBJECT");
        var fields = input["inputFields"]!.ToDictionary(f => f["name"]!.Value<string>()!, f => f["type"]!);
        fields.Keys.Should().BeEquivalentTo("currentPassword", "newPassword");
        fields["currentPassword"]["kind"]!.Value<string>().Should().Be("SCALAR", "an optional parameter is nullable");
        fields["newPassword"]["kind"]!.Value<string>().Should().Be("NON_NULL");

        // The input argument is required when a parameter is required, optional when all parameters are optional.
        var mutations = await FieldsAsync($"{Prefix}AccessTestAccountMutations");
        mutations["resetPassword"]["args"]!.Single(a => a["name"]!.Value<string>() == "input")["type"]!["kind"]!
            .Value<string>().Should().Be("NON_NULL");
        mutations["resetPassword2"]["args"]!.Single(a => a["name"]!.Value<string>() == "input")["type"]!["kind"]!
            .Value<string>().Should().Be("INPUT_OBJECT");
    }

    [Fact]
    public async Task Input_SupportsEnumRecordAndListParameters()
    {
        var input = await TypeAsync($"{Prefix}OperableThingCalibrateMethodInput");
        var fields = input["inputFields"]!.ToDictionary(f => f["name"]!.Value<string>()!, f => f["type"]!);

        fields.Keys.Should().BeEquivalentTo("target", "mode", "specification", "tags");
        fields["target"]["kind"]!.Value<string>().Should().Be("NON_NULL");
        fields["mode"]["name"]!.Value<string>().Should().Be($"{Prefix}OperatingStatus");
        fields["specification"]["name"]!.Value<string>().Should().Be($"{Prefix}SpecificationInput");
        fields["tags"]["kind"]!.Value<string>().Should().Be("LIST");
    }

    [Fact]
    public async Task Result_IsBuiltFromTheDeclaredResult_AndCarriesNoErrorMember()
    {
        var record = await ResultFieldsAsync($"{Prefix}OperableThingCalibrateMethodResult");
        record.Keys.Should().BeEquivalentTo("invocationId", "result");
        record["result"]["name"]!.Value<string>().Should().Be($"{Prefix}Specification");

        var scalar = await ResultFieldsAsync($"{Prefix}OperableThingCalibrate2MethodResult");
        scalar["result"]["name"]!.Value<string>().Should().Be("Int");

        var enumResult = await ResultFieldsAsync($"{Prefix}OperableThingProbeMethodResult");
        enumResult["result"]["name"]!.Value<string>().Should().Be($"{Prefix}OperatingStatus");

        var none = await ResultFieldsAsync($"{Prefix}AccessTestAccountTouchMethodResult");
        none.Keys.Should().BeEquivalentTo("invocationId");
        record.Keys.Concat(scalar.Keys).Concat(none.Keys).Should().NotContain("error").And.NotContain("success");
    }

    [Theory]
    [InlineData("OperableThingMutations")]
    [InlineData("OperableThingChildMutations")]
    public async Task InheritedMethod_AppearsOnceOnTheConcreteType(string mutationType)
    {
        var names = (await FieldsAsync($"{Prefix}{mutationType}")).Keys.ToList();

        names.Where(n => n == "restart").Should().HaveCount(1, "the interface method is inherited once");
        names.Should().Contain(["calibrate", "calibrate2", "probe", "restart"]);
        names.Count.Should().Be(names.Distinct().Count());
    }

    [Fact]
    public async Task InheritedMethod_SharesItsTypesWithTheBaseType()
    {
        var baseField = (await FieldsAsync($"{Prefix}OperableThingMutations"))["calibrate"];
        var childField = (await FieldsAsync($"{Prefix}OperableThingChildMutations"))["calibrate"];

        childField["type"]!["name"]!.Value<string>().Should().Be(baseField["type"]!["name"]!.Value<string>());
    }

    [Fact]
    public async Task TypeWithoutMethods_IsUnchanged_SnapshotOfTheStateBeforeTheMethodMutations()
    {
        var schema = await _fixture.GetService<ISchemaContext>()
            .GetOrCreateAsync(_fixture.GetSystemContext().TenantId);
        var sdl = schema.Print().Replace("\r\n", "\n", StringComparison.Ordinal);

        var golden = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "GraphQL", "Methods",
                "MethodLessTypes.golden.graphql"), TestContext.Current.CancellationToken))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        // Every block of the snapshot (mutation and input type of types without methods) is still in the schema, byte for byte.
        var blocks = Regex.Matches(golden, @"^(type|input) \S+ \{\n.*?\n\}\n", RegexOptions.Singleline | RegexOptions.Multiline)
            .Select(m => m.Value).ToList();
        blocks.Count.Should().BeGreaterThan(10);
        foreach (var block in blocks)
        {
            sdl.Should().Contain(block);
        }
    }

    [Fact]
    public async Task MethodFields_CarryTheMethodCallMarker()
    {
        var schema = await _fixture.GetService<ISchemaContext>()
            .GetOrCreateAsync(_fixture.GetSystemContext().TenantId);
        var mutationType = (ObjectGraphType)schema.AllTypes[$"{Prefix}OperableThingChildMutations"]!;

        var methodFields = mutationType.Fields.Where(f => f.Metadata.ContainsKey("CkMethodCall")).ToList();
        methodFields.Select(f => f.Name).Should().BeEquivalentTo("calibrate", "calibrate2", "probe", "restart");
        mutationType.Fields.Where(f => f.Name is "create" or "update")
            .Should().OnlyContain(f => !f.Metadata.ContainsKey("CkMethodCall"));
        var restart = methodFields.Single(f => f.Name == "restart").Metadata["CkMethodCall"]!;
        restart.ToString().Should().Contain("OperableThing.Restart-1");
    }

    [Fact]
    public async Task MethodWithManyMethods_AllGetAField()
    {
        var fields = await FieldsAsync($"{Prefix}MethodLoadThingMutations");

        fields.Keys.Count(n => n.StartsWith("load", StringComparison.Ordinal)).Should().Be(50);
    }

    private async Task<Dictionary<string, JToken>> ResultFieldsAsync(string typeName)
    {
        var type = await TypeAsync(typeName);
        return type["fields"]!.ToDictionary(f => f["name"]!.Value<string>()!, f => f["type"]!);
    }

    private async Task<Dictionary<string, JToken>> FieldsAsync(string typeName)
    {
        var type = await TypeAsync(typeName);
        return type["fields"]!.ToDictionary(f => f["name"]!.Value<string>()!, f => f);
    }

    private static IEnumerable<string> ArgumentNames(JToken field) =>
        field["args"]!.Select(a => a["name"]!.Value<string>()!);

    private async Task<JToken> TypeAsync(string name)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query {
              __type(name: "{{name}}") {
                kind
                fields { name type { kind name ofType { kind name } } args { name type { kind name ofType { kind name } } } }
                inputFields { name type { kind name ofType { kind name } } }
              }
            }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        var type = JObject.Parse(json).SelectToken("data.__type");
        type.Should().NotBeNull($"the schema must contain {name}");
        return type!;
    }
}
