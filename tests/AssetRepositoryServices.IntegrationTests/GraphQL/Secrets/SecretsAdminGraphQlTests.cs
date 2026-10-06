using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Secrets;

/// <summary>
///     AB#5544 / AB#5535 (handover §2, §7, §12): secret state (<c>keyMissing</c>, <c>setAt</c>) on typed and generic
///     reads, and the secrets overview <c>secrets { inventory summary usages }</c> end to end against MongoDB -
///     including a value protected with a key id that is not in the key ring (restore from another environment)
///     and the role check. Values never appear in a response.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class SecretsAdminGraphQlTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/ServiceCredential";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestServiceCredential";
    private const string UnknownKeyId = "kx";

    private const string ItemSelection = @"
        ckTypeId rtId rtWellKnownName displayName attributePath attributeName required form keyId setAt needsReEntry
        usedBy { dataFlowRtId dataFlowName pipelineRtId pipelineName nodePath match }";

    private readonly GraphQlTestFixture _fixture;

    public SecretsAdminGraphQlTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static string FakeSecret(string label) => $"fake-{label}-{Guid.NewGuid():N}";

    #region Secret state on reads (AB#5535)

    [Fact]
    public async Task TypedAndGenericRead_FreshValue_IsSetWithSetAt()
    {
        var rtId = await CreateAsync(new { name = "state-fresh", password = FakeSecret("pw") });
        var storedSetAt = await ReadStoredSetAtAsync(rtId, "password");

        var typed = await QueryTypedAsync(rtId);
        typed.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        typed.SelectToken("password.keyMissing")!.Value<bool>().Should().BeFalse();
        AssertSetAt(typed.SelectToken("password.setAt")!, storedSetAt);
        typed.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeFalse();
        typed.SelectToken("apiKey.keyMissing")!.Value<bool>().Should().BeFalse();
        typed.SelectToken("apiKey.setAt")!.Type.Should().Be(JTokenType.Null);

        var attributes = await QueryGenericAsync(rtId);
        var password = attributes.Single(a => a["attributeName"]!.Value<string>() == "password");
        password["secretIsSet"]!.Value<bool>().Should().BeTrue();
        password["secretKeyMissing"]!.Value<bool>().Should().BeFalse();
        AssertSetAt(password["secretSetAt"]!, storedSetAt);
        var name = attributes.Single(a => a["attributeName"]!.Value<string>() == "name");
        name["secretKeyMissing"]!.Type.Should().Be(JTokenType.Null);
        name["secretSetAt"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task TypedAndGenericRead_UnknownKeyId_IsKeyMissing_AndCiphertextIsKept()
    {
        var rtId = await CreateAsync(new
        {
            name = "state-key-missing",
            password = FakeSecret("pw"),
            endpoints = new object[] { new { key = "prod", token = FakeSecret("tok") } }
        });
        await MoveToUnknownKeyAsync(rtId, "password");

        var typed = await QueryTypedAsync(rtId);
        typed.SelectToken("password.isSet")!.Value<bool>().Should().BeFalse();
        typed.SelectToken("password.keyMissing")!.Value<bool>().Should().BeTrue();
        typed.SelectToken("endpoints[0].token.isSet")!.Value<bool>().Should().BeTrue();
        typed.SelectToken("endpoints[0].token.keyMissing")!.Value<bool>().Should().BeFalse();

        var attributes = await QueryGenericAsync(rtId);
        var password = attributes.Single(a => a["attributeName"]!.Value<string>() == "password");
        password["value"]!.Type.Should().Be(JTokenType.Null);
        password["secretIsSet"]!.Value<bool>().Should().BeFalse();
        password["secretKeyMissing"]!.Value<bool>().Should().BeTrue();

        // Records in the generic projection carry the state per member.
        var token = attributes.Single(a => a["attributeName"]!.Value<string>() == "endpoints")
            .SelectTokens("value[0].attributes[*]").Single(a => a["attributeName"]!.Value<string>() == "token");
        token["secretIsSet"]!.Value<bool>().Should().BeTrue();
        token["secretKeyMissing"]!.Value<bool>().Should().BeFalse();
        // Record members inside the generic 'value' scalar omit null fields.
        AssertSetAt(token["secretSetAt"], await ReadStoredSetAtAsync(rtId, "endpoints", 0, "token"));

        // Decision 2026-10-06: the ciphertext stays stored.
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "password", CollectionSuffix);
        raw.AsBsonDocument["e"].AsString.Should().StartWith($"enc:v2:{UnknownKeyId}:");
    }

    [Fact]
    public async Task PlaceholderLookingInput_IsAnOrdinaryValue()
    {
        // Decision 2026-10-06: placeholders have no meaning on input; they are encrypted like any other value.
        var rtId = await CreateAsync(new { name = "placeholder", password = "TODO_SET_PASSWORD", apiKey = "<api-key>" });

        var typed = await QueryTypedAsync(rtId);
        typed.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        typed.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeTrue();
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "password", CollectionSuffix);
        raw.AsBsonDocument["e"].AsString.Should().StartWith($"enc:v2:{ServiceCollectionFixture.TestSecretKeyId}:");
    }

    #endregion

    #region Authorization

    [Fact]
    public async Task Secrets_WithoutAdminPanelRole_IsForbidden()
    {
        var result = await _fixture.ExecuteGraphQlAsync("query { secrets { summary { total } } }");

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Forbidden");
        JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("data.secrets")!.Type
            .Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task SecretsInventory_WithoutAdminPanelRole_IsForbidden()
    {
        var result = await _fixture.ExecuteGraphQlAsync(
            "query { secrets { inventory { totalCount items { rtId } } } }",
            user: StreamDataFixture.StreamDataAdminPrincipal);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Forbidden");
    }

    #endregion

    #region Inventory and summary (AB#5544)

    [Fact]
    public async Task Inventory_ListsEverySlot_WithFormKeyIdSetAtAndReEntry()
    {
        var password = FakeSecret("pw");
        var token = FakeSecret("tok");
        var rtId = await CreateAsync(new
        {
            name = "inventory",
            password,
            endpoints = new object[] { new { key = "prod", token }, new { key = "test", label = "no token" } }
        });
        await MoveToUnknownKeyAsync(rtId, "password");

        var (json, connection) = await InventoryAsync($"ckTypeId: \"{CkTypeId}\", search: \"{rtId}\", first: 50");
        json.Should().NotContain(password).And.NotContain(token).And.NotContain("enc:v");

        // primaryEndpoint is not set: an absent record has no member slots.
        connection["totalCount"]!.Value<int>().Should().Be(4, "password, apiKey and two endpoint tokens");
        var items = (JArray)connection["items"]!;
        var byPath = items.ToDictionary(i => i["attributePath"]!.Value<string>()!);
        byPath.Keys.Should().BeEquivalentTo("password", "apiKey", "endpoints[key=prod].token",
            "endpoints[key=test].token");
        byPath["endpoints[key=test].token"]["form"]!.Value<string>().Should().Be("NOT_SET");
        byPath["endpoints[key=test].token"]["needsReEntry"]!.Value<bool>().Should().BeFalse();

        var pw = byPath["password"];
        pw["ckTypeId"]!.Value<string>().Should().Be(CkTypeId);
        pw["rtId"]!.Value<string>().Should().Be(rtId);
        pw["attributeName"]!.Value<string>().Should().Be("Password");
        pw["required"]!.Value<bool>().Should().BeTrue();
        pw["form"]!.Value<string>().Should().Be("KEY_MISSING");
        pw["keyId"]!.Value<string>().Should().Be(UnknownKeyId);
        pw["needsReEntry"]!.Value<bool>().Should().BeTrue();
        ((JArray)pw["usedBy"]!).Should().BeEmpty();

        var prod = byPath["endpoints[key=prod].token"];
        prod["form"]!.Value<string>().Should().Be("ENC_V2");
        prod["keyId"]!.Value<string>().Should().Be(ServiceCollectionFixture.TestSecretKeyId);
        AssertSetAt(prod["setAt"]!, await ReadStoredSetAtAsync(rtId, "endpoints", 0, "token"));
        prod["needsReEntry"]!.Value<bool>().Should().BeFalse();
        prod["attributeName"]!.Value<string>().Should().Be("Endpoints");

        var apiKey = byPath["apiKey"];
        apiKey["form"]!.Value<string>().Should().Be("NOT_SET");
        apiKey["required"]!.Value<bool>().Should().BeFalse();
        apiKey["needsReEntry"]!.Value<bool>().Should().BeFalse();
        apiKey["keyId"]!.Type.Should().Be(JTokenType.Null);
        apiKey["setAt"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task Inventory_FiltersAndPages()
    {
        var rtId = await CreateAsync(new
        {
            name = "inventory-filter",
            password = FakeSecret("pw"),
            apiKey = FakeSecret("api"),
            endpoints = new object[] { new { key = "a", label = "no token" } }
        });
        await MoveToUnknownKeyAsync(rtId, "password");

        var (_, reEntry) = await InventoryAsync($"search: \"{rtId}\", needsReEntry: true");
        reEntry["totalCount"]!.Value<int>().Should().Be(1);
        ((JArray)reEntry["items"]!).Select(i => i["attributePath"]!.Value<string>()).Should().Equal("password");

        var (_, encV2) = await InventoryAsync($"search: \"{rtId}\", forms: [ENC_V2]");
        ((JArray)encV2["items"]!).Select(i => i["attributePath"]!.Value<string>()).Should().Equal("apiKey");

        var (_, missing) = await InventoryAsync($"search: \"{rtId}\", forms: [KEY_MISSING, CORRUPT]");
        missing["totalCount"]!.Value<int>().Should().Be(1);

        // Paging with offset cursors (password, apiKey, endpoints[key=a].token).
        var (_, page1) = await InventoryAsync($"search: \"{rtId}\", first: 2");
        page1["totalCount"]!.Value<int>().Should().Be(3);
        ((JArray)page1["items"]!).Should().HaveCount(2);
        page1.SelectToken("pageInfo.hasNextPage")!.Value<bool>().Should().BeTrue();
        page1.SelectToken("pageInfo.hasPreviousPage")!.Value<bool>().Should().BeFalse();
        var endCursor = page1.SelectToken("pageInfo.endCursor")!.Value<string>();

        var (_, page2) = await InventoryAsync($"search: \"{rtId}\", first: 2, after: \"{endCursor}\"");
        ((JArray)page2["items"]!).Should().ContainSingle();
        page2.SelectToken("pageInfo.hasNextPage")!.Value<bool>().Should().BeFalse();
        page2.SelectToken("pageInfo.hasPreviousPage")!.Value<bool>().Should().BeTrue();

        var paths = ((JArray)page1["items"]!).Concat((JArray)page2["items"]!)
            .Select(i => i["attributePath"]!.Value<string>());
        paths.Should().OnlyHaveUniqueItems().And.HaveCount(3);

        // Unknown CK type = empty page.
        var (_, unknown) = await InventoryAsync("ckTypeId: \"AssetRepositoryIntegrationTest/DoesNotExist\"");
        unknown["totalCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task Inventory_CorruptStoredValue_NeedsReEntry()
    {
        var rtId = await CreateAsync(new { name = "corrupt", password = FakeSecret("pw") });
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "password", CollectionSuffix);
        // An enc:v2 envelope stored as a plain string can never be read.
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "password", raw.AsBsonDocument["e"].AsString,
            CollectionSuffix);

        var (json, connection) = await InventoryAsync($"search: \"{rtId}\", forms: [CORRUPT]");
        json.Should().NotContain("enc:v");
        var item = ((JArray)connection["items"]!).Should().ContainSingle().Subject;
        item["attributePath"]!.Value<string>().Should().Be("password");
        item["needsReEntry"]!.Value<bool>().Should().BeTrue();

        var typed = await QueryTypedAsync(rtId);
        typed.SelectToken("password.isSet")!.Value<bool>().Should().BeFalse();
        typed.SelectToken("password.keyMissing")!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Summary_CountsPerForm_AndPerKeyId()
    {
        var rtId = await CreateAsync(new { name = "summary", password = FakeSecret("pw"), apiKey = FakeSecret("api") });
        await MoveToUnknownKeyAsync(rtId, "password");

        var result = await _fixture.ExecuteGraphQlAsync(@"
            query { secrets {
                summary { total notSet plaintext encV1 encV2 keyMissing corrupt needsReEntry encV2ByKeyId { keyId count } }
                inventory(first: 0) { totalCount }
                reEntry: inventory(first: 0, needsReEntry: true) { totalCount }
            } }", user: GraphQlTestFixture.AdminPanelPrincipal);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);

        var secrets = JObject.Parse(json).SelectToken("data.secrets")!;
        var summary = secrets["summary"]!;
        var total = summary["total"]!.Value<int>();
        total.Should().Be(secrets.SelectToken("inventory.totalCount")!.Value<int>());
        (summary["notSet"]!.Value<int>() + summary["plaintext"]!.Value<int>() + summary["encV1"]!.Value<int>() +
         summary["encV2"]!.Value<int>() + summary["keyMissing"]!.Value<int>() + summary["corrupt"]!.Value<int>())
            .Should().Be(total);
        summary["keyMissing"]!.Value<int>().Should().BeGreaterThanOrEqualTo(1);
        summary["needsReEntry"]!.Value<int>().Should().Be(secrets.SelectToken("reEntry.totalCount")!.Value<int>());

        var byKey = (JArray)summary["encV2ByKeyId"]!;
        byKey.Select(k => k["keyId"]!.Value<string>()).Should().Contain(ServiceCollectionFixture.TestSecretKeyId)
            .And.NotContain(UnknownKeyId);
        byKey.Sum(k => k["count"]!.Value<int>()).Should().Be(summary["encV2"]!.Value<int>());
    }

    #endregion

    #region Usages (AB#5544, Q5)

    [Fact]
    public async Task PipelineSource_WithoutCommunicationModel_ReturnsNoPipelines()
    {
        var pipelines = await _fixture.PipelineSource.Inner.GetPipelinesAsync(_fixture.GetSystemContext(),
            TestContext.Current.CancellationToken);

        pipelines.Should().BeEmpty();
    }

    [Fact]
    public async Task Usages_ExactAndByType_InInventoryAndUsagesQuery()
    {
        var rtId = await CreateAsync(new
        {
            name = "usages",
            password = FakeSecret("pw"),
            apiKey = FakeSecret("api"),
            primaryEndpoint = new { key = "p", token = FakeSecret("tok") }
        });
        var otherRtId = await CreateAsync(new { name = "usages-other", password = FakeSecret("pw") });

        var exactPipeline = OctoObjectId.GenerateNewId();
        var dynamicPipeline = OctoObjectId.GenerateNewId();
        var brokenPipeline = OctoObjectId.GenerateNewId();
        var dataFlow = OctoObjectId.GenerateNewId();
        _fixture.PipelineSource.Add(new PipelineDefinitionInfo(exactPipeline, "send-mail", dataFlow, "notifications",
            $$"""
            triggers:
              - type: FromExecutePipelineCommand@1
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: {{CkTypeId}}
              - type: RevealSecret@1
                ckTypeId: {{CkTypeId}}
                rtId: {{rtId}}
                attributeName: Password
                targetPath: $.smtp.password
              - type: RevealSecret@1
                ckTypeId: {{CkTypeId}}
                rtId: {{rtId}}
                attributeName: primaryEndpoint.token
            """));
        _fixture.PipelineSource.Add(new PipelineDefinitionInfo(dynamicPipeline, "per-config", null, null,
            $$"""
            {"transformations":[{"type":"ForEach@1","transformations":[
              {"type":"RevealSecret@1","ckTypeId":"{{CkTypeId}}","rtIdPath":"$.key.RtId","attributeName":"password"},
              {"type":"RevealSecret@1","ckTypeIdPath":"$.key.CkTypeId","rtIdPath":"$.key.RtId","attributeName":"apiKey"}]}]}
            """));
        _fixture.PipelineSource.Add(new PipelineDefinitionInfo(brokenPipeline, "broken", null, null,
            "transformations: [ { type: RevealSecret@1, rtId: \"unterminated"));
        try
        {
            var (json, connection) = await InventoryAsync($"search: \"{rtId}\"");
            var byPath = ((JArray)connection["items"]!).ToDictionary(i => i["attributePath"]!.Value<string>()!);

            var passwordUsages = (JArray)byPath["password"]["usedBy"]!;
            passwordUsages.Should().HaveCount(2);
            var exact = passwordUsages.Single(u => u["match"]!.Value<string>() == "EXACT");
            exact["pipelineRtId"]!.Value<string>().Should().Be(exactPipeline.ToString());
            exact["pipelineName"]!.Value<string>().Should().Be("send-mail");
            exact["dataFlowRtId"]!.Value<string>().Should().Be(dataFlow.ToString());
            exact["dataFlowName"]!.Value<string>().Should().Be("notifications");
            exact["nodePath"]!.Value<string>().Should().Be("transformations[1]");
            var byType = passwordUsages.Single(u => u["match"]!.Value<string>() == "BY_TYPE");
            byType["pipelineRtId"]!.Value<string>().Should().Be(dynamicPipeline.ToString());
            byType["nodePath"]!.Value<string>().Should().Be("transformations[0].transformations[0]");
            byType["dataFlowRtId"]!.Type.Should().Be(JTokenType.Null);

            ((JArray)byPath["primaryEndpoint.token"]["usedBy"]!).Should().ContainSingle()
                .Which["nodePath"]!.Value<string>().Should().Be("transformations[2]");
            // ckTypeIdPath only: cannot be attributed.
            ((JArray)byPath["apiKey"]["usedBy"]!).Should().BeEmpty();

            // usages(...) for a single slot, and another entity of the same type only gets the BY_TYPE node.
            var result = await _fixture.ExecuteGraphQlAsync($@"
                query {{ secrets {{
                    mine: usages(ckTypeId: ""{CkTypeId}"", rtId: ""{rtId}"", attributePath: ""password"") {{ pipelineRtId match nodePath }}
                    other: usages(ckTypeId: ""{CkTypeId}"", rtId: ""{otherRtId}"", attributePath: ""password"") {{ pipelineRtId match }}
                }} }}", user: GraphQlTestFixture.AdminPanelPrincipal);
            var usagesJson = Serialize(result);
            result.Errors.Should().BeNullOrEmpty(usagesJson);
            var secrets = JObject.Parse(usagesJson).SelectToken("data.secrets")!;
            ((JArray)secrets["mine"]!).Select(u => u["match"]!.Value<string>()).Should()
                .BeEquivalentTo("EXACT", "BY_TYPE");
            ((JArray)secrets["other"]!).Should().ContainSingle().Which["match"]!.Value<string>().Should()
                .Be("BY_TYPE");
        }
        finally
        {
            _fixture.PipelineSource.Remove(exactPipeline);
            _fixture.PipelineSource.Remove(dynamicPipeline);
            _fixture.PipelineSource.Remove(brokenPipeline);
        }
    }

    #endregion

    #region Helpers

    private async Task<string> CreateAsync(object entity)
    {
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestServiceCredentialInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestServiceCredentials {
                        create(entities: $entities) { rtId }
                    }
                }
            }";
        var result = await _fixture.ExecuteGraphQlAsync(mutation,
            JsonSerializer.Serialize(new { entities = new[] { entity } }));
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json)
            .SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.create[0].rtId")!
            .Value<string>()!;
    }

    /// <summary>
    ///     Simulates a restore from another environment: the stored envelope keeps its ciphertext but names a key id
    ///     that is not in this key ring.
    /// </summary>
    private async Task MoveToUnknownKeyAsync(string rtId, string attributeName)
    {
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix);
        var document = raw.AsBsonDocument.DeepClone().AsBsonDocument;
        var envelope = document["e"].AsString;
        var prefix = $"enc:v2:{ServiceCollectionFixture.TestSecretKeyId}:";
        envelope.Should().StartWith(prefix);
        document["e"] = new BsonString($"enc:v2:{UnknownKeyId}:" + envelope[prefix.Length..]);
        await _fixture.SetRawAttributeValueInMongoDb(rtId, attributeName, document, CollectionSuffix);
    }

    /// <summary>
    ///     The stored "set at" (BSON field <c>t</c> of the secret sub-document); null when the storage layer does
    ///     not write it (legacy values, or a MongoDB repository package older than AB#5533 round 2).
    /// </summary>
    private async Task<DateTime?> ReadStoredSetAtAsync(string rtId, string attributeName, int? index = null,
        string? memberName = null)
    {
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix);
        if (index != null)
        {
            var record = raw.AsBsonArray[index.Value].AsBsonDocument;
            var attributes = record.TryGetValue("attributes", out var nested) && nested is BsonDocument nestedDoc
                ? nestedDoc
                : record;
            raw = attributes[memberName!];
        }

        return raw is BsonDocument document && document.TryGetValue("t", out var setAt) && setAt.IsValidDateTime
            ? setAt.ToUniversalTime()
            : null;
    }

    private static void AssertSetAt(JToken? actual, DateTime? stored)
    {
        if (stored == null)
        {
            (actual == null || actual.Type == JTokenType.Null).Should().BeTrue("no 'set at' is stored");
            return;
        }

        actual.Should().NotBeNull();
        actual!.Value<DateTime>().ToUniversalTime().Should().BeCloseTo(stored.Value, TimeSpan.FromMilliseconds(1));
    }

    private async Task<JToken> QueryTypedAsync(string rtId)
    {
        var query = $@"
            query {{
                runtime {{
                    assetRepositoryIntegrationTestServiceCredential(rtId: ""{rtId}"") {{ items {{
                        rtId
                        password {{ isSet keyMissing setAt }}
                        apiKey {{ isSet keyMissing setAt }}
                        endpoints {{ key token {{ isSet keyMissing setAt }} }}
                    }} }}
                }}
            }}";
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        json.Should().NotContain("enc:v");
        return JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredential.items[0]")!;
    }

    private async Task<JArray> QueryGenericAsync(string rtId)
    {
        var query = $@"
            query {{
                runtime {{
                    runtimeEntities(ckId: ""{CkTypeId}"", rtId: ""{rtId}"") {{
                        items {{ attributes {{ items {{ attributeName value secretIsSet secretKeyMissing secretSetAt }} }} }}
                    }}
                }}
            }}";
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        json.Should().NotContain("enc:v");
        return (JArray)JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.items[0].attributes.items")!;
    }

    private async Task<(string Json, JToken Connection)> InventoryAsync(string arguments)
    {
        var result = await _fixture.ExecuteGraphQlAsync($@"
            query {{ secrets {{ inventory({arguments}) {{
                totalCount
                pageInfo {{ hasNextPage hasPreviousPage startCursor endCursor }}
                items {{ {ItemSelection} }}
            }} }} }}", user: GraphQlTestFixture.AdminPanelPrincipal);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return (json, JObject.Parse(json).SelectToken("data.secrets.inventory")!);
    }

    private string Serialize(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine(json);
        return json;
    }

    #endregion
}
