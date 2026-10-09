using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     F1.5-S3 (AB#5922): the constructionKit meta API describes the CK v2 meta model - visibility, derivable,
///     interfaces, method definitions and attribute access - for the ckLanguage 2 integration test model, and returns
///     the v1 defaults for a v1 model.
/// </summary>
[Collection(GraphQlCollection.Name)]
public class CkMetaIntrospectionTests
{
    private const string AccountType = "AssetRepositoryIntegrationTest/AccessTestAccount";

    private readonly GraphQlTestFixture _fixture;

    public CkMetaIntrospectionTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task V2Type_ExposesVisibilityDerivableInterfacesAndAccess()
    {
        var type = await TypeAsync(AccountType, """
            visibility derivable
            declaredInterfaces { rtCkInterfaceId }
            interfaces { rtCkInterfaceId }
            attributes(first: 50) { items { attributeName access attribute { visibility } } }
            """);

        type["visibility"]!.Value<string>().Should().Be("Public");
        type["derivable"]!.Value<string>().Should().Be("Model", "ckLanguage 2 defaults derivable to Model");
        Ids(type["declaredInterfaces"]!).Should().BeEquivalentTo("AssetRepositoryIntegrationTest/Member");
        Ids(type["interfaces"]!).Should().BeEquivalentTo("AssetRepositoryIntegrationTest/Member",
            "AssetRepositoryIntegrationTest/Labeled");
        var access = type.SelectTokens("attributes.items[*]")
            .ToDictionary(a => a["attributeName"]!.Value<string>()!, a => a["access"]!.Value<string>());
        access["passwordHash"].Should().Be("Hidden");
        access["approvalState"].Should().Be("MethodOnly");
        type.SelectTokens("attributes.items[*].attribute.visibility").Should()
            .OnlyContain(v => v.Value<string>() == "Public");
    }

    [Fact]
    public async Task V2Type_ExposesMethodDefinitions()
    {
        var type = await TypeAsync(AccountType, """
            methods {
              methodId qualifiedMethodId kind description visibility
              declaringCkTypeId { semanticVersionedFullName }
              parameters { name valueType isOptional sensitive }
              result { valueType }
              errors { code }
              authorization { roles allowSelf scopes }
              execution { timeoutSeconds idempotent }
            }
            """);

        var methods = type["methods"]!.ToDictionary(m => m["methodId"]!.Value<string>()!);
        methods.Keys.Should().BeEquivalentTo("Lookup-1", "ResetPassword-1", "ResetPassword-2", "Touch-1");

        var reset = methods["ResetPassword-1"];
        reset["qualifiedMethodId"]!.Value<string>().Should().Be(AccountType + ".ResetPassword-1");
        reset["kind"]!.Value<string>().Should().Be("Instance");
        reset["visibility"]!.Value<string>().Should().Be("Public");
        reset.SelectToken("declaringCkTypeId.semanticVersionedFullName")!.Value<string>().Should()
            .Be(AccountType);
        reset["parameters"]!.Select(p => (p["name"]!.Value<string>(), p["isOptional"]!.Value<bool>(),
                p["sensitive"]!.Value<bool>()))
            .Should().Equal(("currentPassword", true, true), ("newPassword", false, true));
        reset["result"]!.Type.Should().Be(JTokenType.Null);
        reset["errors"]!.Select(e => e["code"]!.Value<string>()).Should().Equal("PASSWORD_POLICY_VIOLATION");
        reset.SelectToken("authorization.roles")!.Values<string>().Should().Equal("AccessTestAdmin");
        reset.SelectToken("authorization.allowSelf")!.Value<bool>().Should().BeTrue();
        reset.SelectToken("execution.timeoutSeconds")!.Value<int>().Should().Be(5);

        methods["ResetPassword-2"].SelectToken("result.valueType")!.Value<string>().Should().Be("STRING");
        methods["Touch-1"]["parameters"]!.Should().BeEmpty();
        methods["Touch-1"].SelectToken("execution.timeoutSeconds")!.Value<int>().Should().Be(15);
        methods["Lookup-1"]["kind"]!.Value<string>().Should().Be("Static");
    }

    [Fact]
    public async Task InterfacesQuery_DescribesExtendsMembersAndImplementingTypes()
    {
        var answer = await ExecuteAsync("""
            query {
              constructionKit {
                interfaces(rtCkId: "AssetRepositoryIntegrationTest/Member") {
                  items {
                    rtCkInterfaceId description visibility deprecated
                    extends { semanticVersionedFullName }
                    allExtends { semanticVersionedFullName }
                    attributes { attributeName isOptional attributeValueType }
                    associations { ckAssociationRoleId { semanticVersionedFullName } targetCkTypeId { semanticVersionedFullName } isOptional
                                   declaringCkInterfaceId { semanticVersionedFullName } }
                    methods { methodId }
                    implementingTypes { semanticVersionedFullName }
                  }
                }
              }
            }
            """);

        var member = answer.SelectToken("data.constructionKit.interfaces.items[0]")!;
        member["rtCkInterfaceId"]!.Value<string>().Should().Be("AssetRepositoryIntegrationTest/Member");
        member["visibility"]!.Value<string>().Should().Be("Public");
        member["deprecated"]!.Value<bool>().Should().BeFalse();
        member.SelectTokens("extends[*].semanticVersionedFullName").Values<string>().Should().Equal("AssetRepositoryIntegrationTest/Labeled");
        member.SelectTokens("attributes[*].attributeName").Values<string>().Should().Contain(["name", "label"]);
        member.SelectToken("associations[0].ckAssociationRoleId.semanticVersionedFullName")!.Value<string>()
            .Should().Be("AssetRepositoryIntegrationTest/AccessTestMembership");
        member.SelectToken("associations[0].targetCkTypeId.semanticVersionedFullName")!.Value<string>()
            .Should().Be("AssetRepositoryIntegrationTest/AccessTestGroup");
        member.SelectTokens("implementingTypes[*].semanticVersionedFullName").Values<string>().Should()
            .Equal(AccountType);
        member["methods"]!.Should().BeEmpty();
    }

    [Fact]
    public async Task InterfacesQuery_ListsAllInterfacesOfAModel()
    {
        var answer = await ExecuteAsync("""
            query { constructionKit { interfaces(ckModelIds: ["AssetRepositoryIntegrationTest"]) { totalCount items { rtCkInterfaceId } } } }
            """);

        answer.SelectTokens("data.constructionKit.interfaces.items[*].rtCkInterfaceId").Values<string>().Should()
            .BeEquivalentTo("AssetRepositoryIntegrationTest/Labeled", "AssetRepositoryIntegrationTest/Labeled-2",
                "AssetRepositoryIntegrationTest/Member");
    }

    [Fact]
    public async Task V1Type_ReturnsTheDefaults()
    {
        var type = await TypeAsync("AssetRepositoryIntegrationTest/Customer", """
            visibility derivable declaredInterfaces { rtCkInterfaceId } interfaces { rtCkInterfaceId } methods { methodId }
            attributes(first: 50) { items { access } }
            """);

        type["visibility"]!.Value<string>().Should().Be("Public");
        type["interfaces"]!.Should().BeEmpty();
        type["declaredInterfaces"]!.Should().BeEmpty();
        type["methods"]!.Should().BeEmpty();
        type.SelectTokens("attributes.items[*].access").Should().OnlyContain(a => a.Value<string>() == "ReadWrite");

        var system = await TypeAsync("System/Entity", "visibility derivable interfaces { rtCkInterfaceId } methods { methodId }");
        system["visibility"]!.Value<string>().Should().Be("Public");
        system["derivable"]!.Value<string>().Should().Be("Any", "a v1 model keeps the v1 behaviour");
        system["interfaces"]!.Should().BeEmpty();
        system["methods"]!.Should().BeEmpty();
    }

    [Fact]
    public async Task OtherElements_ExposeVisibility()
    {
        var answer = await ExecuteAsync("""
            query {
              constructionKit {
                records(rtCkId: "AssetRepositoryIntegrationTest/CredentialEntry") { items { visibility derivable } }
                enums(rtCkId: "AssetRepositoryIntegrationTest/OperatingStatus") { items { visibility } }
                associationRoles(rtCkId: "AssetRepositoryIntegrationTest/AccessTestMembership") { items { visibility } }
              }
            }
            """);

        answer.SelectToken("data.constructionKit.records.items[0].visibility")!.Value<string>().Should().Be("Public");
        answer.SelectToken("data.constructionKit.records.items[0].derivable")!.Value<string>().Should().Be("Model");
        answer.SelectToken("data.constructionKit.enums.items[0].visibility")!.Value<string>().Should().Be("Public");
        answer.SelectToken("data.constructionKit.associationRoles.items[0].visibility")!.Value<string>().Should()
            .Be("Public");
    }

    [Fact]
    public async Task Models_ExposeCkLanguageMinEngineVersionAndDependencyRanges()
    {
        var answer = await ExecuteAsync("""
            query { constructionKit { models(first: 100) { items { id { name } ckLanguage minEngineVersion dependencyRanges { range floor } } } } }
            """);

        var models = answer.SelectTokens("data.constructionKit.models.items[*]")
            .ToDictionary(m => m.SelectToken("id.name")!.Value<string>()!);
        var testModel = models["AssetRepositoryIntegrationTest"];
        testModel["ckLanguage"]!.Value<int>().Should().Be(2);
        testModel["minEngineVersion"]!.Value<string>().Should().NotBeNullOrWhiteSpace();
        testModel["dependencyRanges"]!.Type.Should().Be(JTokenType.Null, "the test model is not range-retaining");

        var system = models["System"];
        system["ckLanguage"]!.Value<int>().Should().Be(1, "a v1 model reports the default");
        system["minEngineVersion"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task Schema_HasNoMethodRuntime()
    {
        // F1.5-S1 (AB#5920): method definitions are meta data only - no method mutation fields and no invocation
        // result types until the method runtime (CK v2 Phase 3).
        var answer = await ExecuteAsync("""
            query {
              mutations: __type(name: "AssetRepositoryIntegrationTestAccessTestAccountMutations") { fields { name } }
              schema: __schema { types { name } }
            }
            """);

        answer.SelectTokens("data.mutations.fields[*].name").Values<string>().Should().BeEquivalentTo("create", "update");
        var typeNames = answer.SelectTokens("data.schema.types[*].name").Values<string>().ToList();
        typeNames.Should().NotContain(["CkMethodError", "CkMethodErrorDetail"]);
        typeNames.Should().NotContain(n => n!.EndsWith("ResetPasswordResult") || n.EndsWith("ResetPasswordInput"));
    }

    private static IEnumerable<string> Ids(JToken interfaces) =>
        interfaces.Select(i => i["rtCkInterfaceId"]!.Value<string>()!);

    private async Task<JToken> TypeAsync(string rtCkTypeId, string selection)
    {
        var answer = await ExecuteAsync($$"""
            query { constructionKit { types(rtCkId: "{{rtCkTypeId}}") { items { {{selection}} } } } }
            """);
        var type = answer.SelectToken("data.constructionKit.types.items[0]");
        type.Should().NotBeNull($"type {rtCkTypeId} must exist");
        return type!;
    }

    private async Task<JObject> ExecuteAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }
}
