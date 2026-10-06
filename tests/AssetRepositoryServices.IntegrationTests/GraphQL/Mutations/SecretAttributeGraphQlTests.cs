using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
///     AB#5535 (WP5 of AB#5528, concept §4): the GraphQL contract of Secret attributes end to end against MongoDB -
///     typed and generic create/update/clear/read, records with key carry-over, IS_NULL / IS_NOT_NULL filters,
///     refusals with <c>SecretAttributeNotQueryable</c>, safe mutation echoes, and that no plaintext or envelope
///     ever reaches a response. Uses the <c>ServiceCredential</c> type of the test model and the generated test key
///     ring of <see cref="ServiceCollectionFixture" />.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class SecretAttributeGraphQlTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/ServiceCredential";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestServiceCredential";
    private const string NotQueryableCode = "SecretAttributeNotQueryable";

    private const string TypedSelection = @"
        rtId
        name
        password { isSet }
        apiKey { isSet }
        endpoints { key label token { isSet } }
        primaryEndpoint { key token { isSet } }";

    private readonly GraphQlTestFixture _fixture;

    public SecretAttributeGraphQlTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // Obviously fake, unique per test run - asserted to never appear in any response.
    private static string FakeSecret(string label) => $"fake-{label}-{Guid.NewGuid():N}";

    #region Create and read

    [Fact]
    public async Task TypedCreate_ProjectsIsSetOnly_AndStoresEnvelopes()
    {
        var password = FakeSecret("pw");
        var tokenA = FakeSecret("tokA");
        var tokenP = FakeSecret("tokP");

        var (rtId, json) = await CreateTypedAsync(new
        {
            name = "typed-create",
            password,
            endpoints = new object[] { new { key = "a", token = tokenA }, new { key = "b", label = "no token" } },
            primaryEndpoint = new { key = "p", token = tokenP }
        });

        json.Should().NotContain(password).And.NotContain(tokenA).And.NotContain(tokenP).And.NotContain("enc:v");

        var created = JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.create[0]")!;
        created.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        created.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeFalse();
        created.SelectToken("endpoints[0].token.isSet")!.Value<bool>().Should().BeTrue();
        created.SelectToken("endpoints[1].token.isSet")!.Value<bool>().Should().BeFalse();
        created.SelectToken("primaryEndpoint.token.isSet")!.Value<bool>().Should().BeTrue();

        // Raw storage: an encrypted sub-document with the test key id, never the plaintext.
        var rawPassword = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "password", CollectionSuffix);
        AssertEnvelope(rawPassword);
        rawPassword.ToJson().Should().NotContain(password);
        var rawEndpoints = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "endpoints", CollectionSuffix);
        rawEndpoints.ToJson().Should().NotContain(tokenA);

        var readJson = await QueryTypedAsync(rtId);
        readJson.Should().NotContain(password).And.NotContain(tokenA).And.NotContain("enc:v");
        var read = JObject.Parse(readJson).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredential.items[0]")!;
        read.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        read.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GenericRead_WithoutAttributeNames_ReturnsNullValueAndSecretIsSet()
    {
        var password = FakeSecret("pw");
        var token = FakeSecret("tok");
        var (rtId, _) = await CreateTypedAsync(new
        {
            name = "generic-read",
            password,
            endpoints = new object[] { new { key = "a", token } }
        });

        // attributeNames omitted = every attribute: the case that used to leak credentials.
        var query = $@"
            query {{
                runtime {{
                    runtimeEntities(ckId: ""{CkTypeId}"", rtId: ""{rtId}"") {{
                        items {{
                            attributes {{ items {{ attributeName value secretIsSet }} }}
                        }}
                    }}
                }}
            }}";
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty();
        json.Should().NotContain(password).And.NotContain(token).And.NotContain("enc:v");

        var attributes = (JArray)JObject.Parse(json)
            .SelectToken("data.runtime.runtimeEntities.items[0].attributes.items")!;
        var passwordAttribute = attributes.Single(a => a["attributeName"]!.Value<string>() == "password");
        passwordAttribute["value"]!.Type.Should().Be(JTokenType.Null);
        passwordAttribute["secretIsSet"]!.Value<bool>().Should().BeTrue();

        var apiKeyAttribute = attributes.Single(a => a["attributeName"]!.Value<string>() == "apiKey");
        apiKeyAttribute["value"]!.Type.Should().Be(JTokenType.Null);
        apiKeyAttribute["secretIsSet"]!.Value<bool>().Should().BeFalse();

        var nameAttribute = attributes.Single(a => a["attributeName"]!.Value<string>() == "name");
        nameAttribute["value"]!.Value<string>().Should().Be("generic-read");
        nameAttribute["secretIsSet"]!.Type.Should().Be(JTokenType.Null);

        // Records in the generic projection: the secret member has value null + secretIsSet.
        var endpoints = attributes.Single(a => a["attributeName"]!.Value<string>() == "endpoints");
        var tokenMember = endpoints.SelectTokens("value[0].attributes[*]")
            .Single(a => a["attributeName"]!.Value<string>() == "token");
        tokenMember["value"]!.Type.Should().Be(JTokenType.Null);
        tokenMember["secretIsSet"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task GenericCreate_SetsSecret_AndRejectsNonStringInput()
    {
        var password = FakeSecret("pw");
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime { runtimeEntities { create(entities: $entities) {
                    rtId
                    attributes { items { attributeName value secretIsSet } }
                } } }
            }";
        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    attributes = new object[]
                    {
                        new { attributeName = "name", value = "generic-create" },
                        new { attributeName = "password", value = password }
                    }
                }
            }
        });

        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty();
        json.Should().NotContain(password).And.NotContain("enc:v");
        var attributes = (JArray)JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.create[0].attributes.items")!;
        attributes.Single(a => a["attributeName"]!.Value<string>() == "password")["secretIsSet"]!.Value<bool>()
            .Should().BeTrue();

        // A number is not a secret: refused naming the type only.
        var badVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    attributes = new object[]
                    {
                        new { attributeName = "name", value = "generic-create-bad" },
                        new { attributeName = "password", value = 4711 }
                    }
                }
            }
        });
        var badResult = await _fixture.ExecuteGraphQlAsync(mutation, badVariables);
        badResult.Errors.Should().NotBeNullOrEmpty();
        badResult.Errors![0].Code.Should().Be("ASSET1004");
        badResult.Errors[0].Message.Should().Contain("password").And.NotContain("4711");
    }

    [Fact]
    public async Task Create_WithoutRequiredSecret_FailsWithoutEchoingValues()
    {
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestServiceCredentialInput!]!) {
                runtime { assetRepositoryIntegrationTestServiceCredentials { create(entities: $entities) { rtId } } }
            }";
        var apiKey = FakeSecret("api");
        foreach (var password in new[] { null, "" })
        {
            var variables = JsonSerializer.Serialize(new
            {
                entities = new[] { new { name = "missing-required", password, apiKey } }
            });

            var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);
            var json = Serialize(result);
            result.Errors.Should().NotBeNullOrEmpty("a required secret must be set on create");
            json.Should().NotContain(apiKey);
        }
    }

    [Fact]
    public async Task LegacyPlaintextInStorage_IsNeverProjected()
    {
        // A clear-text string in a Secret slot (data written before the String -> Secret migration) is
        // read as legacy plaintext: every read path projects "is set" only (concept §3.3, §4.1/§4.2).
        var (rtId, _) = await CreateTypedAsync(new
        {
            name = "legacy",
            password = FakeSecret("pw"),
            endpoints = new object[] { new { key = "a", token = FakeSecret("tok") } }
        });
        var legacyPassword = FakeSecret("legacy-pw");
        var legacyToken = FakeSecret("legacy-tok");
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "password", new BsonString(legacyPassword),
            CollectionSuffix);
        var rawEndpoints = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "endpoints", CollectionSuffix);
        var tokenPath = rawEndpoints.AsBsonArray[0].AsBsonDocument.Contains("attributes")
            ? "endpoints.0.attributes.token"
            : "endpoints.0.token";
        await _fixture.SetRawAttributeValueInMongoDb(rtId, tokenPath, new BsonString(legacyToken), CollectionSuffix);

        var typedJson = await QueryTypedAsync(rtId);
        typedJson.Should().NotContain(legacyPassword).And.NotContain(legacyToken);
        var typed = JObject.Parse(typedJson)
            .SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredential.items[0]")!;
        typed.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        typed.SelectToken("endpoints[0].token.isSet")!.Value<bool>().Should().BeTrue();

        var genericQuery = $@"
            query {{
                runtime {{
                    runtimeEntities(ckId: ""{CkTypeId}"", rtId: ""{rtId}"") {{
                        items {{ attributes {{ items {{ attributeName value secretIsSet }} }} }}
                    }}
                }}
            }}";
        var genericResult = await _fixture.ExecuteGraphQlAsync(genericQuery);
        var genericJson = Serialize(genericResult);
        genericResult.Errors.Should().BeNullOrEmpty();
        genericJson.Should().NotContain(legacyPassword).And.NotContain(legacyToken);

        var queryRowsQuery = $@"
            query {{
                runtime {{
                    transientQuery {{
                        simple(ckId: ""{CkTypeId}"", columnPaths: [""name"", ""endpoints[0].key""],
                            fieldFilter: [{{ attributePath: ""name"", operator: EQUALS, comparisonValue: ""legacy"" }}]) {{
                            items {{ rows {{ items {{ cells {{ items {{ attributePath value }} }} }} }} }}
                        }}
                    }}
                }}
            }}";
        var rowsJson = Serialize(await _fixture.ExecuteGraphQlAsync(queryRowsQuery));
        rowsJson.Should().NotContain(legacyPassword).And.NotContain(legacyToken);
    }

    #endregion

    #region Update and clear

    [Fact]
    public async Task TypedUpdate_NullOrEmptySecret_KeepsStoredValue()
    {
        var (rtId, _) = await CreateTypedAsync(new { name = "keep", password = FakeSecret("pw") });
        var envelopeBefore = await ReadEnvelopeAsync(rtId, "password");

        var json = await UpdateTypedAsync(rtId, new { name = "keep-renamed", password = (string?)null, apiKey = "" });

        var updated = JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.update[0]")!;
        updated["name"]!.Value<string>().Should().Be("keep-renamed");
        updated.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        updated.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeFalse();

        (await ReadEnvelopeAsync(rtId, "password")).Should().Be(envelopeBefore,
            "null and \"\" mean unchanged - the stored ciphertext is not touched");
    }

    [Fact]
    public async Task TypedUpdate_NewSecret_ReEncrypts_AndEchoIsSafe()
    {
        var (rtId, _) = await CreateTypedAsync(new { name = "rotate", password = FakeSecret("pw") });
        var envelopeBefore = await ReadEnvelopeAsync(rtId, "password");
        var newPassword = FakeSecret("pw2");
        var apiKey = FakeSecret("api");

        var json = await UpdateTypedAsync(rtId, new { password = newPassword, apiKey });

        json.Should().NotContain(newPassword).And.NotContain(apiKey).And.NotContain("enc:v");
        var updated = JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.update[0]")!;
        updated.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeTrue();
        (await ReadEnvelopeAsync(rtId, "password")).Should().NotBe(envelopeBefore);
        AssertEnvelope(await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "apiKey", CollectionSuffix));
    }

    [Fact]
    public async Task TypedUpdate_ClearSecretAttributes_ClearsOptionalSecret()
    {
        var (rtId, _) = await CreateTypedAsync(new { name = "clear", password = FakeSecret("pw"), apiKey = FakeSecret("api") });

        // Clearing alone (no attribute in the item) is a valid update.
        var json = await UpdateTypedAsync(rtId, new { }, clearSecretAttributes: ["apiKey"]);

        var updated = JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.update[0]")!;
        updated.SelectToken("apiKey.isSet")!.Value<bool>().Should().BeFalse();
        updated.SelectToken("password.isSet")!.Value<bool>().Should().BeTrue();
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "apiKey", CollectionSuffix)).IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task ClearRequiredSecret_IsRejected()
    {
        var password = FakeSecret("pw");
        var (rtId, _) = await CreateTypedAsync(new { name = "clear-required", password });

        var (result, json) = await UpdateTypedRawAsync(rtId, new { }, clearSecretAttributes: ["password"]);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Code.Should().Be("ASSET1004");
        json.Should().Contain("22:", "the engine reports message 22 (required secret cannot be cleared)");
        json.Should().NotContain(password);
        (await ReadEnvelopeAsync(rtId, "password")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SetAndClearSameSecret_IsRejected()
    {
        var (rtId, _) = await CreateTypedAsync(new { name = "set-and-clear", password = FakeSecret("pw") });
        var apiKey = FakeSecret("api");

        var (result, json) = await UpdateTypedRawAsync(rtId, new { apiKey }, clearSecretAttributes: ["apiKey"]);

        result.Errors.Should().NotBeNullOrEmpty();
        json.Should().Contain("23:").And.NotContain(apiKey);
    }

    [Fact]
    public async Task GenericUpdate_SendingBackReadValues_DoesNotClear_AndClearIsExplicit()
    {
        var (rtId, _) = await CreateTypedAsync(new { name = "generic-update", password = FakeSecret("pw"), apiKey = FakeSecret("api") });
        var passwordEnvelope = await ReadEnvelopeAsync(rtId, "password");
        var apiKeyEnvelope = await ReadEnvelopeAsync(rtId, "apiKey");

        var mutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime { runtimeEntities { update(entities: $entities) {
                    rtId
                    attributes { items { attributeName value secretIsSet } }
                } } }
            }";

        // What a client read (value null + secretIsSet) sent back unchanged.
        var echoVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new
                    {
                        ckTypeId = CkTypeId,
                        attributes = new object[]
                        {
                            new { attributeName = "name", value = "generic-update-2", secretIsSet = (bool?)null },
                            new { attributeName = "password", value = (string?)null, secretIsSet = (bool?)true },
                            new { attributeName = "apiKey", value = (string?)null, secretIsSet = (bool?)true }
                        }
                    }
                }
            }
        });
        var echoResult = await _fixture.ExecuteGraphQlAsync(mutation, echoVariables);
        echoResult.Errors.Should().BeNullOrEmpty();
        (await ReadEnvelopeAsync(rtId, "password")).Should().Be(passwordEnvelope);
        (await ReadEnvelopeAsync(rtId, "apiKey")).Should().Be(apiKeyEnvelope);

        // Explicit clear on the generic input.
        var clearVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new { ckTypeId = CkTypeId, attributes = Array.Empty<object>() },
                    clearSecretAttributes = new[] { "apiKey" }
                }
            }
        });
        var clearResult = await _fixture.ExecuteGraphQlAsync(mutation, clearVariables);
        var clearJson = Serialize(clearResult);
        clearResult.Errors.Should().BeNullOrEmpty();
        var attributes = (JArray)JObject.Parse(clearJson).SelectToken("data.runtime.runtimeEntities.update[0].attributes.items")!;
        attributes.Single(a => a["attributeName"]!.Value<string>() == "apiKey")["secretIsSet"]!.Value<bool>()
            .Should().BeFalse();
        attributes.Single(a => a["attributeName"]!.Value<string>() == "password")["secretIsSet"]!.Value<bool>()
            .Should().BeTrue();
    }

    [Fact]
    public async Task RecordArray_ReorderedWithoutSecrets_CarriesSecretsOverByKey()
    {
        var tokenA = FakeSecret("tokA");
        var tokenP = FakeSecret("tokP");
        var (rtId, _) = await CreateTypedAsync(new
        {
            name = "records",
            password = FakeSecret("pw"),
            endpoints = new object[] { new { key = "a", token = tokenA }, new { key = "b" } },
            primaryEndpoint = new { key = "p", token = tokenP }
        });
        var rawBefore = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "endpoints", CollectionSuffix);
        var envelopeA = FindRecordTokenEnvelope(rawBefore, "a");

        // Reordered, labels changed, no token given (null / omitted) -> carried over by record key.
        var json = await UpdateTypedAsync(rtId, new
        {
            endpoints = new object[]
            {
                new { key = "b", label = "second" },
                new { key = "a", label = "first", token = (string?)null }
            },
            primaryEndpoint = new { key = "p", token = "" }
        });

        json.Should().NotContain(tokenA).And.NotContain(tokenP).And.NotContain("enc:v");
        var updated = JObject.Parse(json).SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.update[0]")!;
        var endpoints = (JArray)updated["endpoints"]!;
        endpoints.Single(e => e["key"]!.Value<string>() == "a").SelectToken("token.isSet")!.Value<bool>().Should().BeTrue();
        endpoints.Single(e => e["key"]!.Value<string>() == "b").SelectToken("token.isSet")!.Value<bool>().Should().BeFalse();
        updated.SelectToken("primaryEndpoint.token.isSet")!.Value<bool>().Should().BeTrue();

        var rawAfter = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "endpoints", CollectionSuffix);
        FindRecordTokenEnvelope(rawAfter, "a").Should().Be(envelopeA, "the stored ciphertext is carried over, not re-entered");
    }

    #endregion

    #region Filters and refusals

    [Fact]
    public async Task Filter_IsNullAndIsNotNull_Work()
    {
        var marker = $"filter-{Guid.NewGuid():N}";
        var (withKey, _) = await CreateTypedAsync(new { name = marker, password = FakeSecret("pw"), apiKey = FakeSecret("api") });
        var (withoutKey, _) = await CreateTypedAsync(new { name = marker, password = FakeSecret("pw") });

        async Task<List<string>> QueryAsync(string op)
        {
            var query = $@"
                query {{
                    runtime {{
                        assetRepositoryIntegrationTestServiceCredential(fieldFilter: [
                            {{ attributePath: ""name"", operator: EQUALS, comparisonValue: ""{marker}"" }},
                            {{ attributePath: ""apiKey"", operator: {op} }}
                        ]) {{ items {{ rtId apiKey {{ isSet }} }} }}
                    }}
                }}";
            var result = await _fixture.ExecuteGraphQlAsync(query);
            result.Errors.Should().BeNullOrEmpty();
            return JObject.Parse(Serialize(result))
                .SelectTokens("data.runtime.assetRepositoryIntegrationTestServiceCredential.items[*].rtId")
                .Select(t => t.Value<string>()!).ToList();
        }

        (await QueryAsync("IS_NOT_NULL")).Should().BeEquivalentTo(withKey);
        (await QueryAsync("IS_NULL")).Should().BeEquivalentTo(withoutKey);
    }

    [Theory]
    [InlineData(@"fieldFilter: [{ attributePath: ""password"", operator: EQUALS, comparisonValue: ""x"" }]", "password")]
    [InlineData(@"fieldFilter: [{ attributePath: ""password"", operator: LIKE, comparisonValue: ""x"" }]", "password")]
    [InlineData(@"sortOrder: [{ attributePath: ""password"", sortOrder: ASCENDING }]", "password")]
    [InlineData(@"searchFilter: { type: ATTRIBUTE_FILTER, attributePaths: [""apiKey""], searchTerm: ""x"" }", "apiKey")]
    [InlineData(@"aggregations: { countAttributePaths: [""password""] }", "password")]
    [InlineData(@"aggregations: { groupBy: { groupByAttributePaths: [""apiKey""] } }", "apiKey")]
    [InlineData(@"fieldFilter: [{ attributePath: ""endpoints.token"", operator: EQUALS, comparisonValue: ""x"" }]", "endpoints.token")]
    public async Task TypedQuery_SecretNotQueryable_IsRefused(string arguments, string expectedPath)
    {
        var query = $@"
            query {{
                runtime {{
                    assetRepositoryIntegrationTestServiceCredential({arguments}) {{ items {{ rtId }} }}
                }}
            }}";

        var result = await _fixture.ExecuteGraphQlAsync(query);

        AssertNotQueryable(result, expectedPath);
    }

    [Fact]
    public async Task GenericQuery_SortOnSecret_IsRefused()
    {
        var query = $@"
            query {{
                runtime {{
                    runtimeEntities(ckId: ""{CkTypeId}"",
                        sortOrder: [{{ attributePath: ""password"", sortOrder: DESCENDING }}]) {{ items {{ rtId }} }}
                }}
            }}";

        AssertNotQueryable(await _fixture.ExecuteGraphQlAsync(query), "password");
    }

    [Fact]
    public async Task TransientQuery_SecretColumn_IsRefused()
    {
        var query = $@"
            query {{
                runtime {{
                    transientQuery {{
                        simple(ckId: ""{CkTypeId}"", columnPaths: [""name"", ""password""]) {{
                            items {{ rows {{ items {{ cells {{ items {{ attributePath value }} }} }} }} }}
                        }}
                    }}
                }}
            }}";

        AssertNotQueryable(await _fixture.ExecuteGraphQlAsync(query), "password");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("endpoints[0].token")]
    public async Task QueryRowMutation_SecretCell_IsRefused_WithoutEchoingTheValue(string secretPath)
    {
        // Secrets are not query columns (concept §4.4), so a persistent-query row cannot write them either;
        // the refusal happens before the mapping errors, which echo cell values.
        var createQuery = $@"
            mutation {{
                runtime {{ systemSimpleRtQuerys {{
                    create(entities: [{{ name: ""secret-rows"", queryCkTypeId: ""{CkTypeId}"", columns: [""name""] }}]) {{ rtId }}
                }} }}
            }}";
        var createResult = await _fixture.ExecuteGraphQlAsync(createQuery);
        createResult.Errors.Should().BeNullOrEmpty();
        var queryRtId = JObject.Parse(Serialize(createResult))
            .SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>();
        var secret = FakeSecret("row");

        var insert = $@"
            mutation {{
                runtime {{ runtimeQuery(rtId: ""{queryRtId}"") {{
                    create(entities: [{{ ckTypeId: ""{CkTypeId}"", cells: [
                        {{ attributePath: ""name"", value: ""secret-row"" }},
                        {{ attributePath: ""{secretPath}"", value: ""{secret}"" }}
                    ] }}]) {{ ckTypeId }}
                }} }}
            }}";
        var result = await _fixture.ExecuteGraphQlAsync(insert);

        Serialize(result).Should().NotContain(secret);
        AssertNotQueryable(result, secretPath);
    }

    #endregion

    #region Schema

    [Fact]
    public async Task Schema_TypedFieldsUseOctoSecretState_AndInputsUseString()
    {
        var query = @"
            query {
                output: __type(name: ""AssetRepositoryIntegrationTestServiceCredential"") {
                    fields { name type { kind name ofType { kind name } } }
                }
                input: __type(name: ""AssetRepositoryIntegrationTestServiceCredentialInput"") {
                    inputFields { name type { kind name } }
                }
                update: __type(name: ""AssetRepositoryIntegrationTestServiceCredentialInputUpdate"") {
                    inputFields { name type { kind name ofType { kind name ofType { kind name } } } }
                }
                record: __type(name: ""AssetRepositoryIntegrationTestCredentialEntry"") {
                    fields { name type { kind name ofType { kind name } } }
                }
                state: __type(name: ""OctoSecretState"") {
                    fields { name type { kind ofType { name } } }
                }
            }";

        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = Serialize(result);
        _fixture.OutputHelper?.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty();
        var data = JObject.Parse(json)["data"]!;

        JToken Field(string type, string collection, string name) =>
            data[type]![collection]!.Single(f => f["name"]!.Value<string>() == name);

        var password = Field("output", "fields", "password")["type"]!;
        password["kind"]!.Value<string>().Should().Be("NON_NULL");
        password.SelectToken("ofType.name")!.Value<string>().Should().Be("OctoSecretState");

        var apiKey = Field("output", "fields", "apiKey")["type"]!;
        apiKey["kind"]!.Value<string>().Should().Be("OBJECT");
        apiKey["name"]!.Value<string>().Should().Be("OctoSecretState");

        Field("input", "inputFields", "password")["type"]!["name"]!.Value<string>().Should().Be("String");
        Field("input", "inputFields", "apiKey")["type"]!["name"]!.Value<string>().Should().Be("String");

        var clear = Field("update", "inputFields", "clearSecretAttributes")["type"]!;
        clear["kind"]!.Value<string>().Should().Be("LIST");
        clear.SelectToken("ofType.kind")!.Value<string>().Should().Be("NON_NULL");
        clear.SelectToken("ofType.ofType.name")!.Value<string>().Should().Be("String");

        var token = Field("record", "fields", "token")["type"]!;
        token["name"]!.Value<string>().Should().Be("OctoSecretState");

        data["state"]!["fields"]!.Select(f => f["name"]!.Value<string>()).Should().BeEquivalentTo("isSet");
    }

    [Fact]
    public async Task Schema_ScalarSelectionOfSecretField_FailsValidation()
    {
        // Old documents that select a secret as a scalar must fail loudly instead of leaking (concept §4.1).
        var query = @"query { runtime { assetRepositoryIntegrationTestServiceCredential { items { password } } } }";

        var result = await _fixture.ExecuteGraphQlAsync(query);

        result.Errors.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helpers

    private async Task<(string RtId, string Json)> CreateTypedAsync(object entity)
    {
        var mutation = $@"
            mutation ($entities: [AssetRepositoryIntegrationTestServiceCredentialInput!]!) {{
                runtime {{
                    assetRepositoryIntegrationTestServiceCredentials {{
                        create(entities: $entities) {{ {TypedSelection} }}
                    }}
                }}
            }}";
        var variables = JsonSerializer.Serialize(new { entities = new[] { entity } });

        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);

        var rtId = JObject.Parse(json)
            .SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.create[0].rtId")!
            .Value<string>()!;
        return (rtId, json);
    }

    private async Task<string> UpdateTypedAsync(string rtId, object item, string[]? clearSecretAttributes = null)
    {
        var (result, json) = await UpdateTypedRawAsync(rtId, item, clearSecretAttributes);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }

    private async Task<(ExecutionResult Result, string Json)> UpdateTypedRawAsync(string rtId, object item,
        string[]? clearSecretAttributes)
    {
        var mutation = $@"
            mutation ($entities: [AssetRepositoryIntegrationTestServiceCredentialInputUpdate!]!) {{
                runtime {{
                    assetRepositoryIntegrationTestServiceCredentials {{
                        update(entities: $entities) {{ {TypedSelection} }}
                    }}
                }}
            }}";
        var variables = clearSecretAttributes == null
            ? JsonSerializer.Serialize(new { entities = new[] { new { rtId, item } } })
            : JsonSerializer.Serialize(new { entities = new[] { new { rtId, item, clearSecretAttributes } } });

        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);
        return (result, Serialize(result));
    }

    private async Task<string> QueryTypedAsync(string rtId)
    {
        var query = $@"
            query {{
                runtime {{
                    assetRepositoryIntegrationTestServiceCredential(rtId: ""{rtId}"") {{ items {{ {TypedSelection} }} }}
                }}
            }}";
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }

    private async Task<string?> ReadEnvelopeAsync(string rtId, string attributeName)
    {
        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix);
        return raw is BsonDocument document && document.TryGetValue("e", out var envelope) ? envelope.AsString : null;
    }

    private static string? FindRecordTokenEnvelope(BsonValue rawRecords, string key)
    {
        foreach (var element in rawRecords.AsBsonArray)
        {
            var record = element.AsBsonDocument;
            var attributes = record.TryGetValue("attributes", out var nested) && nested is BsonDocument nestedDoc
                ? nestedDoc
                : record;
            if (attributes.TryGetValue("key", out var keyValue) && keyValue.IsString && keyValue.AsString == key)
            {
                return attributes.TryGetValue("token", out var token) && token is BsonDocument tokenDoc
                    ? tokenDoc["e"].AsString
                    : null;
            }
        }

        return null;
    }

    private static void AssertEnvelope(BsonValue raw)
    {
        raw.IsBsonDocument.Should().BeTrue($"a secret is stored as an encrypted sub-document, got {raw.BsonType}");
        var document = raw.AsBsonDocument;
        document["_t"].AsString.Should().Be("OctoSecret");
        document["e"].AsString.Should().StartWith($"enc:v2:{ServiceCollectionFixture.TestSecretKeyId}:");
    }

    private static void AssertNotQueryable(ExecutionResult result, string expectedPath)
    {
        result.Errors.Should().NotBeNullOrEmpty();
        var error = result.Errors!.First();
        error.Code.Should().Be(NotQueryableCode, error.Message);
        error.Extensions.Should().NotBeNull();
        error.Extensions!["attributePath"]!.ToString().Should().Be(expectedPath);
    }

    private string Serialize(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine(json);
        return json;
    }

    #endregion
}
