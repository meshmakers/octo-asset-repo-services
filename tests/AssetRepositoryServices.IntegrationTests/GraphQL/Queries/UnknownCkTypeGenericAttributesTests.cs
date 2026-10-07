using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     AB#5535: a generic list query must not fail because one entity's CK type is no longer in the tenant's CK cache
///     (live: an outdated <c>System.Communication/AiConfiguration</c> entity in tenant <c>meshmakers</c>). End to end
///     the unknown CK element is simulated by a record whose stored <c>ckRecordId</c> the model does not have (the
///     engine's type filter keeps entities of an unknown CK type out of a typed MongoDB query; that projection path is
///     covered by <c>UnknownCkTypeAttributeProjectionTests</c>). Recognisable secrets stay masked.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class UnknownCkTypeGenericAttributesTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/ServiceCredential";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestServiceCredential";

    private readonly GraphQlTestFixture _fixture;

    public UnknownCkTypeGenericAttributesTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task ListQuery_WithEmptyAttributeNames_ReturnsNoAttributes()
    {
        var password = $"fake-empty-{Guid.NewGuid():N}";
        var first = await CreateAsync(new { name = "empty-filter-1", password });
        var second = await CreateAsync(new { name = "empty-filter-2", password });

        var json = await QueryAsync(first, second, "attributes(attributeNames: []) { items { attributeName } }");

        var items = (JArray)JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.items")!;
        items.Should().HaveCount(2);
        items.SelectTokens("[*].attributes.items[*]").Should().BeEmpty();
    }

    [Fact]
    public async Task ListQuery_WithARecordOfAnUnknownCkRecord_DoesNotFail_AndMasksItsSecret()
    {
        var password = $"fake-pw-{Guid.NewGuid():N}";
        var token = $"fake-tok-{Guid.NewGuid():N}";
        var healthy = await CreateAsync(new { name = "record-healthy", password });
        var outdated = await CreateAsync(new
        {
            name = "record-outdated",
            password,
            endpoints = new object[] { new { key = "a", label = "kept", token } }
        });

        // Simulate a record of a CK record that a model update removed.
        var rawEndpoints = await _fixture.ReadRawAttributeValueFromMongoDb(outdated, "endpoints", CollectionSuffix);
        var storedRecordId = rawEndpoints.AsBsonArray[0].AsBsonDocument["ckRecordId"].AsString;
        await _fixture.SetRawAttributeValueInMongoDb(outdated, "endpoints.0.ckRecordId",
            new BsonString(storedRecordId.Replace("/", "/Removed")), CollectionSuffix);

        var json = await QueryAsync(healthy, outdated, "attributes { items { attributeName value secretIsSet } }");

        json.Should().NotContain(password).And.NotContain(token).And.NotContain("enc:v");
        var items = (JArray)JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.items")!;
        items.Should().HaveCount(2);

        var attributes = (JArray)items.Single(i => i["rtId"]!.Value<string>() == outdated)
            .SelectToken("attributes.items")!;
        attributes.Single(a => a["attributeName"]!.Value<string>() == "password")["secretIsSet"]!.Value<bool>()
            .Should().BeTrue();
        var members = attributes.Single(a => a["attributeName"]!.Value<string>() == "endpoints")
            .SelectTokens("value[0].attributes[*]").ToList();
        members.Single(m => m["attributeName"]!.Value<string>() == "label")["value"]!.Value<string>()
            .Should().Be("kept");
        var tokenMember = members.Single(m => m["attributeName"]!.Value<string>() == "token");
        tokenMember["value"]!.Type.Should().Be(JTokenType.Null);
        tokenMember["secretIsSet"]!.Value<bool>().Should().BeTrue();
    }

    private async Task<string> CreateAsync(object entity)
    {
        const string mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestServiceCredentialInput!]!) {
                runtime { assetRepositoryIntegrationTestServiceCredentials { create(entities: $entities) { rtId } } }
            }";
        var variables = JsonSerializer.Serialize(new { entities = new[] { entity } });
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json)
            .SelectToken("data.runtime.assetRepositoryIntegrationTestServiceCredentials.create[0].rtId")!
            .Value<string>()!;
    }

    private async Task<string> QueryAsync(string knownRtId, string unknownRtId, string attributesSelection)
    {
        var query = $@"
            query {{
                runtime {{
                    runtimeEntities(ckId: ""{CkTypeId}"", rtIds: [""{knownRtId}"", ""{unknownRtId}""]) {{
                        items {{ rtId ckTypeId {attributesSelection} }}
                    }}
                }}
            }}";
        ExecutionResult result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }
}
