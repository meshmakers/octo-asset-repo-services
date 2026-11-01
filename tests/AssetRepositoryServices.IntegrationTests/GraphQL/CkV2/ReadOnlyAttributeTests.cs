using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#8047: an attribute with <c>access: ReadOnly</c> is set once on create and is never changed by a generic write -
///     the engine rule <c>AttributeAccess.IsGenericallyWritable(access, isCreate)</c>. The generic and the typed update and
///     the query-row update refuse it with <c>ATTRIBUTE_NOT_WRITABLE</c> and leave the stored value unchanged; create and
///     query-row create accept it. Test attribute: <c>AssetRepositoryIntegrationTest/AccessTestAccount.ExternalReference</c>.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class ReadOnlyAttributeTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/AccessTestAccount";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestAccessTestAccount";
    private const string Original = "ref-original";
    private const string Changed = "ref-changed";

    private readonly GraphQlTestFixture _fixture;

    public ReadOnlyAttributeTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task CkMeta_ReportsReadOnly_AndTheInputTypeOffersTheField()
    {
        var answer = await ExecuteOkAsync($$"""
            query { constructionKit { types(rtCkId: "{{CkTypeId}}") {
              items { attributes(first: 50) { items { attributeName access } } } } } }
            """);
        answer.SelectTokens("data.constructionKit.types.items[0].attributes.items[*]")
            .Single(a => a["attributeName"]!.Value<string>() == "externalReference")["access"]!.Value<string>()
            .Should().Be("ReadOnly");
    }

    [Fact]
    public async Task GenericCreate_SetsTheReadOnlyValue()
    {
        var rtId = await GenericCreateAsync("ro-create");

        (await StoredAsync(rtId)).Should().Be(Original);
    }

    [Fact]
    public async Task GenericUpdate_OfTheReadOnlyValue_IsRefused_AndTheValueIsUnchanged()
    {
        var rtId = await GenericCreateAsync("ro-generic-update");

        var result = await _fixture.ExecuteGraphQlAsync("""
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
                        ckTypeId = CkTypeId,
                        attributes = new[] { new { attributeName = "externalReference", value = Changed } }
                    }
                }
            }
        }));

        AssertNotWritable(result);
        (await StoredAsync(rtId)).Should().Be(Original);
    }

    [Fact]
    public async Task TypedUpdate_OfTheReadOnlyValue_IsRefused_AndTheValueIsUnchanged()
    {
        var rtId = await GenericCreateAsync("ro-typed-update");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { assetRepositoryIntegrationTestAccessTestAccounts {
              update(entities: [{ rtId: "{{rtId}}", item: { externalReference: "{{Changed}}" } }]) { rtId } } } }
            """);

        AssertNotWritable(result);
        (await StoredAsync(rtId)).Should().Be(Original);
    }

    [Fact]
    public async Task Update_OfOtherAttributes_StillWorks_AndKeepsTheReadOnlyValue()
    {
        var rtId = await GenericCreateAsync("ro-other-update");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { assetRepositoryIntegrationTestAccessTestAccounts {
              update(entities: [{ rtId: "{{rtId}}", item: { label: "relabelled" } }]) { rtId } } } }
            """);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        (await StoredAsync(rtId)).Should().Be(Original);
    }

    [Fact]
    public async Task TypedCreate_SetsTheReadOnlyValue()
    {
        var answer = await ExecuteOkAsync($$"""
            mutation { runtime { assetRepositoryIntegrationTestAccessTestAccounts {
              create(entities: [{ name: "ro-typed-create", externalReference: "{{Original}}" }]) { rtId } } } }
            """);
        var rtId = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestAccessTestAccounts.create[0].rtId")!
            .Value<string>()!;

        (await StoredAsync(rtId)).Should().Be(Original);
    }

    [Fact]
    public async Task QueryRowUpdate_OfTheReadOnlyValue_IsRefused_QueryRowCreate_IsAccepted()
    {
        var queryRtId = await CreatePersistentQueryAsync();

        var create = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeQuery(rtId: "{{queryRtId}}") {
              create(entities: [{ ckTypeId: "{{CkTypeId}}", cells: [
                { attributePath: "name", value: "ro-row" }, { attributePath: "externalReference", value: "{{Original}}" }
              ] }]) { ckTypeId }
            } } }
            """);
        create.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(create));

        var rtId = await GenericCreateAsync("ro-row-update");
        var update = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeQuery(rtId: "{{queryRtId}}") {
              update(entities: [{ rtId: "{{rtId}}", item: { ckTypeId: "{{CkTypeId}}", cells: [
                { attributePath: "externalReference", value: "{{Changed}}" }
              ] } }]) { ckTypeId }
            } } }
            """);

        AssertNotWritable(update);
        (await StoredAsync(rtId)).Should().Be(Original);
    }

    private async Task<string> GenericCreateAsync(string name)
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityInput!]!) {
              runtime { runtimeEntities { create(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    attributes = new[]
                    {
                        new { attributeName = "name", value = name },
                        new { attributeName = "externalReference", value = Original }
                    }
                }
            }
        }));
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
    }

    private async Task<string> CreatePersistentQueryAsync()
    {
        var answer = await ExecuteOkAsync($$"""
            mutation { runtime { systemSimpleRtQuerys {
              create(entities: [{ name: "readonly-rows", queryCkTypeId: "{{CkTypeId}}", columns: ["name", "externalReference"] }]) { rtId }
            } } }
            """);
        return answer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private async Task<string?> StoredAsync(string rtId)
    {
        var value = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "externalReference", CollectionSuffix);
        return value.IsBsonNull ? null : value.AsString;
    }

    private void AssertNotWritable(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        var error = result.Errors!.Should().Contain(e => e.Code == "ATTRIBUTE_NOT_WRITABLE", json).Subject;
        error.Message.Should().Contain("access: ReadOnly");
    }

    private async Task<JObject> ExecuteOkAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }
}
