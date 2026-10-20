using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#7025: the generic <c>create</c> of a type that has a static method with <c>replacesGenericCreate: true</c>
///     (AB#7024) - declared on the type or inherited - is refused with ONE error
///     (<c>GENERIC_CREATE_REPLACED_BY_METHOD</c>, parameters <c>ckTypeId</c> and <c>methodId</c>) in the typed and in the
///     untyped create; nothing is written. Other types, update and delete are untouched. Test types
///     <c>ManagedThing</c> (flagged), <c>ManagedThingChild</c> (derived) and <c>ToolThing</c> (static method without the
///     flag).
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class GenericCreateReplacedByMethodTests
{
    private const string Code = "GENERIC_CREATE_REPLACED_BY_METHOD";
    private const string ManagedThing = "AssetRepositoryIntegrationTest/ManagedThing";
    private const string ManagedThingChild = "AssetRepositoryIntegrationTest/ManagedThingChild";
    private const string ToolThing = "AssetRepositoryIntegrationTest/ToolThing";
    private const string ManagedThingMethod = "AssetRepositoryIntegrationTest/ManagedThing.Provision-1";

    private const string UntypedCreate = """
        mutation ($entities: [RtEntityInput!]!) {
          runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }
        """;

    private readonly GraphQlTestFixture _fixture;

    public GenericCreateReplacedByMethodTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task TypedCreate_OfFlaggedType_IsRefusedWithOneErrorNamingTheMethod_AndWritesNothing()
    {
        var before = await CountAsync("assetRepositoryIntegrationTestManagedThing");

        var answer = await ExecuteAsync("""
            mutation {
              runtime { assetRepositoryIntegrationTestManagedThings {
                create(entities: [{ name: "typed-refused" }]) { rtId }
              } }
            }
            """);

        AssertRefusal(answer, ManagedThing, ManagedThingMethod);
        (await CountAsync("assetRepositoryIntegrationTestManagedThing")).Should().Be(before);
    }

    [Fact]
    public async Task UntypedCreate_OfFlaggedType_IsRefusedWithTheSameCodeAndParameters_AndWritesNothing()
    {
        var before = await CountAsync("assetRepositoryIntegrationTestManagedThing");

        var answer = await ExecuteAsync(UntypedCreate, EntityVariables(ManagedThing, "untyped-refused"));

        AssertRefusal(answer, ManagedThing, ManagedThingMethod);
        (await CountAsync("assetRepositoryIntegrationTestManagedThing")).Should().Be(before);
    }

    [Fact]
    public async Task TypedAndUntypedRefusal_AreIdentical()
    {
        var typed = await ExecuteAsync("""
            mutation {
              runtime { assetRepositoryIntegrationTestManagedThings {
                create(entities: [{ name: "a" }]) { rtId }
              } }
            }
            """);
        var untyped = await ExecuteAsync(UntypedCreate, EntityVariables(ManagedThing, "b"));

        typed.SelectToken("errors")!.ToString().Should().Be(untyped.SelectToken("errors")!.ToString());
    }

    [Fact]
    public async Task Create_OfDerivedConcreteType_IsRefusedWithTheInheritedMethodId()
    {
        var before = await CountAsync("assetRepositoryIntegrationTestManagedThingChild");

        var typed = await ExecuteAsync("""
            mutation {
              runtime { assetRepositoryIntegrationTestManagedThingChilds {
                create(entities: [{ name: "child-typed" }]) { rtId }
              } }
            }
            """);
        var untyped = await ExecuteAsync(UntypedCreate, EntityVariables(ManagedThingChild, "child-untyped"));

        AssertRefusal(typed, ManagedThingChild, ManagedThingMethod);
        AssertRefusal(untyped, ManagedThingChild, ManagedThingMethod);
        (await CountAsync("assetRepositoryIntegrationTestManagedThingChild")).Should().Be(before);
    }

    [Fact]
    public async Task Refusal_IsByteIdenticalForTwoCallers_AndCarriesNoEntityData()
    {
        var anonymous = await ExecuteAsync(UntypedCreate, EntityVariables(ManagedThing, "caller-one-secret-name"));
        var admin = await ExecuteAsync(UntypedCreate, EntityVariables(ManagedThing, "caller-two-other-name"),
            GraphQlTestFixture.AdminPanelPrincipal);

        var first = anonymous.SelectToken("errors")!.ToString();
        first.Should().Be(admin.SelectToken("errors")!.ToString());
        first.Should().NotContain("secret-name").And.NotContain("other-name")
            .And.NotContain("stack", "no stack or exception data is sent").And.NotContain("exception");
        anonymous.SelectToken("errors")!.Should().HaveCount(1);
        anonymous.SelectToken("errors[0].extensions")!.Children<JProperty>().Select(p => p.Name)
            .Should().BeEquivalentTo("code", "codes", "parameters");
    }

    [Fact]
    public async Task Create_OfTypeWithMethodWithoutTheFlag_WorksAsBefore()
    {
        var typed = await ExecuteAsync("""
            mutation {
              runtime { assetRepositoryIntegrationTestToolThings {
                create(entities: [{ name: "tool-typed" }]) { rtId }
              } }
            }
            """);
        var untyped = await ExecuteAsync(UntypedCreate, EntityVariables(ToolThing, "tool-untyped"));

        typed.SelectToken("errors").Should().BeNull(typed.ToString());
        typed.SelectToken("data.runtime.assetRepositoryIntegrationTestToolThings.create[0].rtId").Should().NotBeNull();
        untyped.SelectToken("errors").Should().BeNull(untyped.ToString());
        untyped.SelectToken("data.runtime.runtimeEntities.create[0].rtId").Should().NotBeNull();
    }

    [Fact]
    public async Task Create_OfTypeWithoutMethods_WorksAsBefore()
    {
        var answer = await ExecuteAsync(UntypedCreate, EntityVariables("AssetRepositoryIntegrationTest/AccessTestGroup",
            "plain-group"));

        answer.SelectToken("errors").Should().BeNull(answer.ToString());
    }

    [Fact]
    public async Task UpdateAndDelete_OfFlaggedType_AreNotRefused()
    {
        // An entity of the flagged type as its method would have written it: copied from a ToolThing document.
        var tool = await ExecuteAsync(UntypedCreate, EntityVariables(ToolThing, "to-be-copied"));
        var toolRtId = tool.SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
        var rtId = await _fixture.CloneRawEntityInMongoDb(toolRtId, "AssetRepositoryIntegrationTestToolThing",
            "ToolThing", "AssetRepositoryIntegrationTestManagedThing", "ManagedThing");

        var typedUpdate = await ExecuteAsync($$"""
            mutation {
              runtime { assetRepositoryIntegrationTestManagedThings {
                update(entities: [{ rtId: "{{rtId}}", item: { name: "renamed" } }]) { rtId name }
              } }
            }
            """);
        var untypedUpdate = await ExecuteAsync("""
            mutation ($entities: [RtEntityUpdate!]!) {
              runtime { runtimeEntities { update(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new
                    {
                        ckTypeId = ManagedThing,
                        attributes = new[] { new { attributeName = "name", value = "renamed-again" } }
                    }
                }
            }
        }));
        var delete = await ExecuteAsync($$"""
            mutation {
              runtime { runtimeEntities { delete(entities: [{ rtId: "{{rtId}}", ckTypeId: "{{ManagedThing}}" }]) } }
            }
            """);

        typedUpdate.SelectToken("errors").Should().BeNull(typedUpdate.ToString());
        typedUpdate.SelectToken("data.runtime.assetRepositoryIntegrationTestManagedThings.update[0].name")!
            .Value<string>().Should().Be("renamed");
        untypedUpdate.SelectToken("errors").Should().BeNull(untypedUpdate.ToString());
        delete.SelectToken("errors").Should().BeNull(delete.ToString());
    }

    [Fact]
    public async Task CkMeta_ShowsTheFlagOnTheMethod_AndV1QueriesAreUnchanged()
    {
        var flagged = await ExecuteAsync($$"""
            query {
              constructionKit { types(rtCkId: "{{ManagedThing}}") { items { methods { methodId replacesGenericCreate } } } }
            }
            """);
        var child = await ExecuteAsync($$"""
            query {
              constructionKit { types(rtCkId: "{{ManagedThingChild}}") { items { methods { methodId replacesGenericCreate } } } }
            }
            """);
        var tool = await ExecuteAsync($$"""
            query {
              constructionKit { types(rtCkId: "{{ToolThing}}") { items { methods { methodId replacesGenericCreate } } } }
            }
            """);
        // The v1 selection of a CK type (no CK v2 member) still resolves unchanged.
        var v1 = await ExecuteAsync($$"""
            query {
              constructionKit { types(rtCkId: "{{ManagedThing}}") { items { rtCkTypeId isAbstract isFinal } } }
            }
            """);

        flagged.SelectToken("errors").Should().BeNull(flagged.ToString());
        flagged.SelectToken("data.constructionKit.types.items[0].methods")!
            .Select(m => (m["methodId"]!.Value<string>(), m["replacesGenericCreate"]!.Value<bool>()))
            .Should().Equal(("Provision-1", true));
        child.SelectToken("data.constructionKit.types.items[0].methods")!
            .Select(m => (m["methodId"]!.Value<string>(), m["replacesGenericCreate"]!.Value<bool>()))
            .Should().Equal(("Provision-1", true));
        tool.SelectToken("data.constructionKit.types.items[0].methods")!
            .Select(m => (m["methodId"]!.Value<string>(), m["replacesGenericCreate"]!.Value<bool>()))
            .Should().Equal(("Lookup-1", false));
        v1.SelectToken("errors").Should().BeNull(v1.ToString());
        v1.SelectToken("data.constructionKit.types.items[0].rtCkTypeId").Should().NotBeNull();
    }

    private static void AssertRefusal(JObject answer, string ckTypeId, string methodId)
    {
        var errors = answer.SelectToken("errors")!;
        errors.Should().HaveCount(1, answer.ToString());
        errors[0]!.SelectToken("extensions.code")!.Value<string>().Should().Be(Code);
        errors[0]!.SelectToken("extensions.parameters.ckTypeId")!.Value<string>().Should().Be(ckTypeId);
        errors[0]!.SelectToken("extensions.parameters.methodId")!.Value<string>().Should().Be(methodId);
        errors[0]!["message"]!.Value<string>().Should().Contain(methodId);
        (answer.SelectToken("data.runtime.runtimeEntities.create")?.Type ?? JTokenType.Null).Should()
            .Be(JTokenType.Null, "data.<field> is null on a refused call");
    }

    private static string EntityVariables(string ckTypeId, string name) =>
        JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new { ckTypeId, attributes = new[] { new { attributeName = "name", value = name } } }
            }
        });

    private async Task<int> CountAsync(string field)
    {
        var answer = await ExecuteAsync($"query {{ runtime {{ {field} {{ totalCount }} }} }}");
        answer.SelectToken("errors").Should().BeNull(answer.ToString());
        return answer.SelectToken($"data.runtime.{field}.totalCount")!.Value<int>();
    }

    private async Task<JObject> ExecuteAsync(string query, string? variables = null, ClaimsPrincipal? user = null)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query, variables, user);
        return JObject.Parse(_fixture.SerializeGraphQl(result));
    }
}
