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
///     AB#8055: a whole-record update (typed or generic) rebuilds the record from its input fields. Members the caller may not
///     write on update (<c>ReadOnly</c>, <c>Hidden</c>, <c>MethodOnly</c>) keep their stored values: a single record by
///     position, a record array by its record key; a record array without a key is refused when it would drop such a value.
///     Test type <c>AssetRepositoryIntegrationTest/RecordAccessThing</c> (records <c>AccessTestDetail</c>,
///     <c>AccessTestInner</c>, <c>AccessTestKeyedEntry</c>). All values are dummies.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class RecordMemberPreservationTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/RecordAccessThing";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestRecordAccessThing";
    private const string TypedField = "assetRepositoryIntegrationTestRecordAccessThings";
    private const string StoredRef = "dummy-ref-0001";
    private const string StoredInnerRef = "dummy-inner-ref-0001";
    private const string StoredNote = "dummy-hidden-note-0001";
    private const string StoredState = "dummy-method-state-0001";

    private readonly GraphQlTestFixture _fixture;

    public RecordMemberPreservationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // The repro of the bug: record with Label (ReadWrite) and Ref (ReadOnly), update with only Label, Ref stays.
    [Fact]
    public async Task TypedUpdate_OfTheWholeRecord_KeepsReadOnlyHiddenAndMethodOnlyMembers()
    {
        var rtId = await CreateWithDetailAsync("typed-detail");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { detail: { label: "label-2", inner: { text: "text-2" } } } }]) { rtId } } } }
            """);

        AssertOk(result);
        var detail = await StoredRecordAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-2");
        Member(detail, "ref").Should().Be(StoredRef);
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        var inner = detail["attributes"]["inner"].AsBsonDocument;
        Member(inner, "text").Should().Be("text-2");
        Member(inner, "innerRef").Should().Be(StoredInnerRef);
    }

    [Fact]
    public async Task GenericUpdate_OfTheWholeRecord_KeepsReadOnlyHiddenAndMethodOnlyMembers()
    {
        var rtId = await CreateWithDetailAsync("generic-detail");

        var result = await GenericUpdateAsync(rtId, "detail", new { label = "label-3" });

        AssertOk(result);
        var detail = await StoredRecordAsync(rtId, "detail");
        Member(detail, "label").Should().Be("label-3");
        Member(detail, "ref").Should().Be(StoredRef);
        Member(detail, "note").Should().Be(StoredNote);
        Member(detail, "state").Should().Be(StoredState);
        // Inner was omitted: the record is rebuilt without it (a writable member), as before.
        detail["attributes"].AsBsonDocument.Contains("inner").Should().BeFalse();
    }

    [Fact]
    public async Task Update_ThatWritesAProtectedMemberExplicitly_IsStillRefused()
    {
        var rtId = await CreateWithDetailAsync("explicit-ref");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { detail: { label: "x", ref: "changed" } } }]) { rtId } } } }
            """);

        AssertNotWritable(result, "access: ReadOnly");
        Member(await StoredRecordAsync(rtId, "detail"), "ref").Should().Be(StoredRef);
    }

    [Fact]
    public async Task Update_WithNullForTheRecord_StillRemovesTheWholeRecord()
    {
        var rtId = await CreateWithDetailAsync("null-detail");

        var result = await GenericUpdateAsync(rtId, "detail", null);

        AssertOk(result);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "detail", CollectionSuffix)).IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Update_OfAKeyedRecordArray_KeepsProtectedMembersOfMatchingElements()
    {
        var rtId = await CreateAsync("keyed", """
            keyedEntries: [{ key: "k1", label: "l1", ref: "dummy-k1-ref" }, { key: "k2", ref: "dummy-k2-ref" }]
            """);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { keyedEntries: [{ key: "k2", label: "l2" }, { key: "k3", label: "l3" }] } }]) { rtId } } } }
            """);

        AssertOk(result);
        var entries = (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "keyedEntries", CollectionSuffix)).AsBsonArray
            .Select(e => e.AsBsonDocument).ToList();
        entries.Select(e => Member(e, "key")).Should().Equal("k2", "k3");
        Member(entries[0], "label").Should().Be("l2");
        Member(entries[0], "ref").Should().Be("dummy-k2-ref");
        Member(entries[1], "ref").Should().BeNull();
    }

    [Fact]
    public async Task Update_OfARecordArrayWithoutKey_IsRefused_WhenAStoredElementHoldsAProtectedValue()
    {
        var rtId = await CreateAsync("unkeyed", """entries: [{ label: "e1", ref: "dummy-e1-ref" }]""");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { entries: [{ label: "e2" }] } }]) { rtId } } } }
            """);

        AssertNotWritable(result, "access: ReadOnly");
        var entries = (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "entries", CollectionSuffix)).AsBsonArray;
        entries.Should().HaveCount(1);
        Member(entries[0].AsBsonDocument, "ref").Should().Be("dummy-e1-ref");
    }

    [Fact]
    public async Task Update_OfARecordArrayWithoutKey_IsAllowed_WhenNoProtectedValueIsStored()
    {
        var rtId = await CreateAsync("unkeyed-empty", """entries: [{ label: "e1" }]""");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { entries: [{ label: "e2" }, { label: "e3" }] } }]) { rtId } } } }
            """);

        AssertOk(result);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "entries", CollectionSuffix)).AsBsonArray
            .Select(e => Member(e.AsBsonDocument, "label")).Should().Equal("e2", "e3");
    }

    [Fact]
    public async Task Update_OfAnotherAttribute_LeavesTheRecordUntouched()
    {
        var rtId = await CreateWithDetailAsync("other-attribute");

        var result = await GenericUpdateAsync(rtId, "name", "renamed");

        AssertOk(result);
        var detail = await StoredRecordAsync(rtId, "detail");
        Member(detail, "ref").Should().Be(StoredRef);
        Member(detail, "note").Should().Be(StoredNote);
    }

    // The bug's second case: clearSecretAttributes on a Secret that the caller may not write.
    [Fact]
    public async Task ClearSecretAttributes_OfAMethodOnlySecret_IsRefused()
    {
        var rtId = await CreateAsync("clear-secret", string.Empty);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeEntities {
              update(entities: [{ rtId: "{{rtId}}", item: { ckTypeId: "{{CkTypeId}}", attributes: [] }, clearSecretAttributes: ["token"] }]) { rtId } } } }
            """);

        AssertNotWritable(result, "access: MethodOnly");
    }

    private async Task<string> CreateWithDetailAsync(string name)
    {
        var rtId = await CreateAsync(name,
            $$"""detail: { label: "label-1", ref: "{{StoredRef}}", inner: { text: "text-1", innerRef: "{{StoredInnerRef}}" } }""");
        // Hidden and MethodOnly members are never generically writable; the owning service writes them through the
        // repository. Simulate that write.
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "detail.attributes.note", new BsonString(StoredNote),
            CollectionSuffix);
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "detail.attributes.state", new BsonString(StoredState),
            CollectionSuffix);
        return rtId;
    }

    private async Task<string> CreateAsync(string name, string fields)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TypedField}} {
              create(entities: [{ name: "ab8055-{{name}}" {{(fields.Length > 0 ? ", " + fields : string.Empty)}} }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{TypedField}.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> GenericUpdateAsync(string rtId, string attributeName, object? value)
    {
        return _fixture.ExecuteGraphQlAsync("""
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
                    item = new { ckTypeId = CkTypeId, attributes = new[] { new { attributeName, value } } }
                }
            }
        }));
    }

    private async Task<BsonDocument> StoredRecordAsync(string rtId, string attributeName)
    {
        return (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, attributeName, CollectionSuffix)).AsBsonDocument;
    }

    private static string? Member(BsonDocument record, string name)
    {
        return record["attributes"].AsBsonDocument.TryGetValue(name, out var value) && !value.IsBsonNull
            ? value.AsString
            : null;
    }

    private void AssertOk(ExecutionResult result)
    {
        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
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
