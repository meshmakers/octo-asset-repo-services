using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#8322: a query-row update (<c>runtimeQuery(rtId) { update }</c>) writes record members path by path. It changes
///     exactly the written path: the other members of the record - in particular the <c>MethodOnly</c> and <c>Hidden</c>
///     members the caller cannot see, and the <c>ReadOnly</c> ones it may not change - keep their stored values; record
///     array elements are addressed by position and changed in place (with or without a record key). An explicit value
///     for a protected member is refused as on a direct attribute write. Test type
///     <c>AssetRepositoryIntegrationTest/RecordAccessThing</c> (records <c>AccessTestDetail</c>, <c>AccessTestInner</c>,
///     <c>AccessTestKeyedDetail</c>). Hidden and MethodOnly values are written straight into MongoDB, as the owning
///     service would. All values are dummies.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class QueryRowRecordMemberPreservationTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/RecordAccessThing";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestRecordAccessThing";
    private const string TypedField = "assetRepositoryIntegrationTestRecordAccessThings";
    private const string StoredRef = "dummy-ref-8322";
    private const string StoredInnerRef = "dummy-inner-ref-8322";
    private const string StoredNote = "dummy-hidden-note-8322";
    private const string StoredState = "dummy-method-state-8322";
    private const string SecretPlain = "dummy-secret-8322";

    private readonly GraphQlTestFixture _fixture;

    public QueryRowRecordMemberPreservationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // The repro of the bug: before AB#8322 the update stored { label: "label-2" } as the whole record.
    [Fact]
    public async Task Update_OfARecordMember_KeepsTheOtherMembers_AlsoMethodOnlyHiddenAndReadOnly()
    {
        var rtId = await CreateWithDetailAsync("record-member");
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("detail.label", "label-2"));

        AssertOk(result);
        var detail = await StoredDocumentAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-2");
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        Member(detail, "ref").Should().Be(StoredRef);
        var inner = detail["attributes"]["inner"].AsBsonDocument;
        Member(inner, "text").Should().Be("text-1");
        Member(inner, "innerRef").Should().Be(StoredInnerRef);
    }

    [Fact]
    public async Task Update_OfANestedRecordMember_KeepsTheMembersOnEveryLevel()
    {
        var rtId = await CreateWithDetailAsync("nested-member");
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("detail.inner.text", "text-2"));

        AssertOk(result);
        var detail = await StoredDocumentAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-1");
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        var inner = detail["attributes"]["inner"].AsBsonDocument;
        Member(inner, "text").Should().Be("text-2");
        Member(inner, "innerRef").Should().Be(StoredInnerRef);
    }

    [Fact]
    public async Task Update_OfARecordArrayElementMember_KeepsTheProtectedMembersOfThatAndTheOtherElements()
    {
        var rtId = await CreateWithKeyedDetailsAsync("keyed-element");
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("keyedDetails[1].label", "l2-new"));

        AssertOk(result);
        var elements = await StoredElementsAsync(rtId, "keyedDetails");
        elements.Select(e => Member(e, "key")).Should().Equal("k1", "k2");
        Member(elements[0], "label").Should().Be("l1");
        Member(elements[0], "note").Should().Be(StoredNote);
        Member(elements[1], "label").Should().Be("l2-new");
        Member(elements[1], "note").Should().Be(StoredNote + "-k2");
        Member(elements[1], "state").Should().Be(StoredState);
    }

    // AB#8141 refuses a keyless record array (message 141) because whole elements cannot be matched by key. A query row
    // addresses one element by its position and changes it in place, so there is nothing to match and nothing to refuse.
    [Fact]
    public async Task Update_OfAKeylessRecordArrayElementMember_ChangesThatElementInPlace_AndKeepsItsProtectedMembers()
    {
        var rtId = await CreateAsync("keyless-element", """entries: [{ label: "e1", ref: "dummy-e1-ref" }, { label: "e2" }]""");
        await SetRawAsync(rtId, "entries.0.attributes.note", StoredNote);
        await SetRawAsync(rtId, "entries.0.attributes.state", StoredState);
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("entries[0].label", "e1-new"));

        AssertOk(result);
        var elements = await StoredElementsAsync(rtId, "entries");
        elements.Should().HaveCount(2);
        Member(elements[0], "label").Should().Be("e1-new");
        Member(elements[0], "ref").Should().Be("dummy-e1-ref");
        Member(elements[0], "note").Should().Be(StoredNote);
        Member(elements[0], "state").Should().Be(StoredState);
        Member(elements[1], "label").Should().Be("e2");
    }

    // Secret members are not copied into the update; the engine carries them over (AB#5533): a single record by position,
    // a record array element by its record key (a record with Secret members always declares one, compiler message 76).
    [Fact]
    public async Task Update_OfARecordMember_KeepsTheStoredSecretMembers_OfASingleRecordAndOfEveryArrayElement()
    {
        var rtId = await CreateAsync("secret-members", $$"""
            keyedDetail: { key: "p", label: "pl", apiKey: "{{SecretPlain}}-p" },
            keyedDetails: [{ key: "k1", label: "l1", apiKey: "{{SecretPlain}}-k1" }, { key: "k2", label: "l2", apiKey: "{{SecretPlain}}-k2" }]
            """);
        await SetRawAsync(rtId, "keyedDetail.attributes.note", StoredNote);
        var single = await StoredDocumentAsync(rtId, "keyedDetail");
        var elements = await StoredElementsAsync(rtId, "keyedDetails");
        var storedSingleSecret = single["attributes"]["apiKey"];
        var storedElementSecrets = elements.Select(e => e["attributes"]["apiKey"]).ToList();
        storedSingleSecret.IsBsonNull.Should().BeFalse();
        storedElementSecrets.Should().AllSatisfy(v => v.IsBsonNull.Should().BeFalse());
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("keyedDetail.label", "pl-new"), ("keyedDetails[1].label", "l2-new"));

        AssertOk(result);
        _fixture.SerializeGraphQl(result).Should().NotContain(SecretPlain);
        single = await StoredDocumentAsync(rtId, "keyedDetail");
        Member(single, "label").Should().Be("pl-new");
        Member(single, "note").Should().Be(StoredNote);
        single["attributes"]["apiKey"].Should().Be(storedSingleSecret);
        elements = await StoredElementsAsync(rtId, "keyedDetails");
        Member(elements[1], "label").Should().Be("l2-new");
        elements.Select(e => e["attributes"]["apiKey"]).Should().Equal(storedElementSecrets);
    }

    [Theory]
    [InlineData("detail.note", "access: Hidden")]
    [InlineData("detail.state", "access: MethodOnly")]
    [InlineData("detail.ref", "access: ReadOnly")]
    [InlineData("keyedDetails[0].state", "access: MethodOnly")]
    [InlineData("keyedDetails[0].note", "access: Hidden")]
    public async Task Update_ThatWritesAProtectedMember_IsRefused_AndChangesNothing(string path, string accessText)
    {
        var rtId = await CreateWithDetailAsync("explicit-" + path);
        await SetKeyedDetailsAsync(rtId);
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("detail.label", "label-2"), (path, "changed"));

        AssertNotWritable(result, accessText);
        var detail = await StoredDocumentAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-1");
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        Member(detail, "ref").Should().Be(StoredRef);
        var elements = await StoredElementsAsync(rtId, "keyedDetails");
        Member(elements[0], "state").Should().BeNull();
        Member(elements[0], "note").Should().Be(StoredNote);
    }

    // AC "the same refusal as on a direct attribute write": the generic update of the record answers the same code.
    [Fact]
    public async Task DirectWrite_OfTheSameProtectedMember_GivesTheSameRefusal()
    {
        var rtId = await CreateWithDetailAsync("direct-write");
        var queryRtId = await CreatePersistentQueryAsync();

        var queryRow = await UpdateRowAsync(queryRtId, rtId, ("detail.state", "changed"));
        var direct = await _fixture.ExecuteGraphQlAsync("""
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
                        attributes = new[] { new { attributeName = "detail", value = new { state = "changed" } } }
                    }
                }
            }
        }));

        AssertNotWritable(queryRow, "access: MethodOnly");
        AssertNotWritable(direct, "access: MethodOnly");
        Member(await StoredDocumentAsync(rtId, "detail"), "state").Should().Be(StoredState);
    }

    [Fact]
    public async Task Update_OfAMemberOfARecordThatIsNotStored_CreatesTheRecord()
    {
        var rtId = await CreateAsync("no-record", string.Empty);
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("detail.label", "label-new"));

        AssertOk(result);
        var detail = await StoredDocumentAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-new");
        detail["attributes"].AsBsonDocument.ElementCount.Should().Be(1);
    }

    [Fact]
    public async Task Update_OfAnotherAttribute_LeavesTheRecordUntouched()
    {
        var rtId = await CreateWithDetailAsync("other-attribute");
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await UpdateRowAsync(queryRtId, rtId, ("name", "ab8322-renamed"));

        AssertOk(result);
        var detail = await StoredDocumentAsync(rtId, "detail");
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        Member(detail, "label").Should().Be("label-1");
    }

    private async Task<string> CreateWithDetailAsync(string name)
    {
        var rtId = await CreateAsync(name,
            $$"""detail: { label: "label-1", ref: "{{StoredRef}}", inner: { text: "text-1", innerRef: "{{StoredInnerRef}}" } }""");
        await SetRawAsync(rtId, "detail.attributes.note", StoredNote);
        await SetRawAsync(rtId, "detail.attributes.state", StoredState);
        return rtId;
    }

    private async Task<string> CreateWithKeyedDetailsAsync(string name)
    {
        var rtId = await CreateAsync(name, string.Empty);
        await SetKeyedDetailsAsync(rtId);
        return rtId;
    }

    // Hidden and MethodOnly members are never generically writable; the owning service writes them through the
    // repository. Simulate that write.
    private async Task SetKeyedDetailsAsync(string rtId)
    {
        var elements = new BsonArray
        {
            KeyedDetail("k1", "l1", new BsonDocument("note", StoredNote)),
            KeyedDetail("k2", "l2", new BsonDocument { { "note", StoredNote + "-k2" }, { "state", StoredState } })
        };
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "keyedDetails", elements, CollectionSuffix);
    }

    private static BsonDocument KeyedDetail(string key, string label, BsonDocument protectedMembers)
    {
        var attributes = new BsonDocument { { "key", key }, { "label", label } };
        attributes.AddRange(protectedMembers);
        return new BsonDocument
        {
            { "attributes", attributes },
            { "ckRecordId", "AssetRepositoryIntegrationTest/AccessTestKeyedDetail" }
        };
    }

    private Task SetRawAsync(string rtId, string path, string value)
    {
        return _fixture.SetRawAttributeValueInMongoDb(rtId, path, new BsonString(value), CollectionSuffix);
    }

    private async Task<string> CreateAsync(string name, string fields)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              create(entities: [{ name: "ab8322-{{name}}" {{(fields.Length > 0 ? ", " + fields : string.Empty)}} }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{TypedField}.create[0].rtId")!.Value<string>()!;
    }

    private async Task<string> CreatePersistentQueryAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys {
              create(entities: [{ name: "ab8322-rows", queryCkTypeId: "{{CkTypeId}}",
                columns: ["name", "detail.label", "detail.inner.text", "keyedDetail.label", "keyedDetails[*].label", "entries[*].label"] }]) { rtId }
            } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> UpdateRowAsync(string queryRtId, string rtId, params (string Path, string Value)[] cells)
    {
        return _fixture.ExecuteGraphQlAsync("""
            mutation ($queryRtId: OctoObjectId!, $entities: [RtQueryRowUpdate!]!) {
              runtime { runtimeQuery(rtId: $queryRtId) { update(entities: $entities) { ckTypeId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            queryRtId,
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new
                    {
                        ckTypeId = CkTypeId,
                        cells = cells.Select(c => new { attributePath = c.Path, value = c.Value }).ToArray()
                    }
                }
            }
        }));
    }

    private async Task<BsonDocument> StoredDocumentAsync(string rtId, string attributeName)
    {
        return (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix)).AsBsonDocument;
    }

    private async Task<List<BsonDocument>> StoredElementsAsync(string rtId, string attributeName)
    {
        return (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix)).AsBsonArray
            .Select(e => e.AsBsonDocument).ToList();
    }

    private static string? Member(BsonDocument record, string name)
    {
        return record["attributes"].AsBsonDocument.TryGetValue(name, out var value) && !value.IsBsonNull
            ? value.AsString
            : null;
    }

    private void AssertOk(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        json.Should().NotContain("dummy-");
    }

    private void AssertNotWritable(ExecutionResult result, string accessText)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        var error = result.Errors!.Should().Contain(e => e.Code == "ATTRIBUTE_NOT_WRITABLE", json).Subject;
        error.Message.Should().Contain(accessText);
        json.Should().NotContain("dummy-");
    }
}
