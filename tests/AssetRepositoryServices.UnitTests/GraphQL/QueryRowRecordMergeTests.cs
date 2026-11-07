using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8322: which record attributes a query-row update merges with the stored value, and what the copy carries
///     (everything except Secret members, which the engine carries over itself).
/// </summary>
public class QueryRowRecordMergeTests
{
    private const string TenantId = "test";
    private readonly ICkCacheService _ckCacheService = A.Fake<ICkCacheService>();
    private readonly CkRecordGraph _inner;
    private readonly CkRecordGraph _detail;
    private readonly CkRecordGraph _owner;

    public QueryRowRecordMergeTests()
    {
        _inner = Record("Test/Inner", Attribute("Text", AttributeValueTypesDto.String),
            Attribute("Token", AttributeValueTypesDto.Secret));
        _detail = Record("Test/Detail", Attribute("Label", AttributeValueTypesDto.String),
            Attribute("Note", AttributeValueTypesDto.String, CkAttributeAccessDto.Hidden),
            Attribute("State", AttributeValueTypesDto.String, CkAttributeAccessDto.MethodOnly),
            Attribute("ApiKey", AttributeValueTypesDto.Secret),
            Attribute("Inner", AttributeValueTypesDto.Record, valueCkRecordId: _inner.CkRecordId));
        // A record graph stands in for the entity's type graph: both expose AllAttributesByName.
        _owner = Record("Test/Owner", Attribute("Name", AttributeValueTypesDto.String),
            Attribute("Detail", AttributeValueTypesDto.Record, valueCkRecordId: _detail.CkRecordId),
            Attribute("Entries", AttributeValueTypesDto.RecordArray, valueCkRecordId: _detail.CkRecordId),
            Attribute("Untouched", AttributeValueTypesDto.Record, valueCkRecordId: _detail.CkRecordId));
        Register(_inner);
        Register(_detail);
    }

    [Fact]
    public void MemberPaths_SelectTheirRecordAttributes_OnlyForMemberPaths()
    {
        var selected = QueryRowRecordMerge.RecordAttributesWithMemberPaths(_owner,
        [
            "detail.label", "entries[0].label", "entries[*].inner.text", "detail", "name", "unknown.label",
            "relatesTo.testOwner->detail.label", "relatesTo.testOwner::totalCount", null, ""
        ]);

        selected.Should().BeEquivalentTo("Detail", "Entries");
    }

    [Fact]
    public void Copy_KeepsHiddenAndMethodOnlyMembers_AndLeavesSecretMembersOut_OnEveryLevel()
    {
        var stored = new RtEntity();
        stored.SetAttributeRawValue("Detail", DetailRecord("label-1"));
        var document = new RtEntity();

        QueryRowRecordMerge.CopyStoredRecords(_ckCacheService, TenantId, _owner, document, stored, ["Detail"]);

        var copy = (RtRecord)document.Attributes["Detail"]!;
        copy.Attributes.Should().ContainKeys("Label", "Note", "State", "Inner").And.NotContainKey("ApiKey");
        copy.Attributes["Note"].Should().Be("note");
        copy.Attributes["State"].Should().Be("state");
        var inner = (RtRecord)copy.Attributes["Inner"]!;
        inner.Attributes.Should().ContainKey("Text").And.NotContainKey("Token");
    }

    [Fact]
    public void Copy_OfARecordArray_CopiesEveryElementInOrder_AndDoesNotShareTheStoredRecords()
    {
        var stored = new RtEntity();
        var storedElements = new List<object?> { DetailRecord("e1"), DetailRecord("e2") };
        stored.SetAttributeRawValue("Entries", storedElements);
        var document = new RtEntity();

        QueryRowRecordMerge.CopyStoredRecords(_ckCacheService, TenantId, _owner, document, stored, ["Entries"]);

        var copies = ((IEnumerable<object?>)document.Attributes["Entries"]!).Cast<RtRecord>().ToList();
        copies.Select(c => c.Attributes["Label"]).Should().Equal("e1", "e2");
        copies.Should().AllSatisfy(c => c.Attributes.Should().ContainKey("Note").And.NotContainKey("ApiKey"));
        copies[0].SetAttributeRawValue("Label", "changed");
        ((RtRecord)storedElements[0]!).Attributes["Label"].Should().Be("e1");
    }

    [Fact]
    public void Copy_LeavesOutAttributesWithoutAStoredValue_AndDoesNothingWithoutAStoredEntity()
    {
        var document = new RtEntity();

        QueryRowRecordMerge.CopyStoredRecords(_ckCacheService, TenantId, _owner, document, new RtEntity(), ["Detail"]);
        QueryRowRecordMerge.CopyStoredRecords(_ckCacheService, TenantId, _owner, document, null, ["Detail"]);

        document.Attributes.Should().BeEmpty();
    }

    private static RtRecord DetailRecord(string label)
    {
        var inner = new RtRecord();
        inner.SetAttributeRawValue("Text", "text");
        inner.SetAttributeRawValue("Token", "stored-secret");
        var detail = new RtRecord();
        detail.SetAttributeRawValue("Label", label);
        detail.SetAttributeRawValue("Note", "note");
        detail.SetAttributeRawValue("State", "state");
        detail.SetAttributeRawValue("ApiKey", "stored-secret");
        detail.SetAttributeRawValue("Inner", inner);
        return detail;
    }

    private void Register(CkRecordGraph record)
    {
        CkRecordGraph? found = record;
        A.CallTo(() => _ckCacheService.TryGetCkRecord(TenantId, record.CkRecordId, out found)).Returns(true)
            .AssignsOutAndRefParameters(record);
    }

    private static CkRecordGraph Record(string id, params CkTypeAttributeGraph[] attributes)
    {
        return new CkRecordGraph(new CkId<CkRecordId>(id), false, false, [], null, [], [],
            attributes.ToDictionary(a => a.CkAttributeId), "test");
    }

    private static CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto valueType,
        CkAttributeAccessDto access = CkAttributeAccessDto.ReadWrite, CkId<CkRecordId>? valueCkRecordId = null)
    {
        return new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/" + name), name, null, valueType,
            valueCkRecordId, null, null, null, null, true, null) { Access = access };
    }
}
