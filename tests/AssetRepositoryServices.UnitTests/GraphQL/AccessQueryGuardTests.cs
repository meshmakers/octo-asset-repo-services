using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     CK v2 Phase 0 review fixes in <see cref="AccessQueryGuard" />: hidden-name cache per CK model graph (M8), fail
///     closed for unknown targets / unloaded tenants (L11), entity-selector keys (L10).
/// </summary>
public class AccessQueryGuardTests
{
    private const string TenantId = "meshtest";

    private readonly ICkCacheService _ckCacheService = A.Fake<ICkCacheService>();
    private List<CkTypeGraph> _types = [];

    public AccessQueryGuardTests()
    {
        A.CallTo(() => _ckCacheService.GetCkTypes(TenantId)).ReturnsLazily(() => _types);
        A.CallTo(() => _ckCacheService.GetCkRecords(TenantId)).Returns([HiddenRecord()]);
        CkTypeGraph? none = null;
        A.CallTo(() => _ckCacheService.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out none)).Returns(false);
    }

    [Fact]
    public void HiddenNames_AreComputedOncePerModelGraph()
    {
        AccessQueryGuard.GetHiddenAttributeNames(_ckCacheService, TenantId).Should().Contain("PasswordHash");
        AccessQueryGuard.GetHiddenAttributeNames(_ckCacheService, TenantId);
        AccessQueryGuard.GetHiddenAttributeNames(_ckCacheService, TenantId);

        A.CallTo(() => _ckCacheService.GetCkRecords(TenantId)).MustHaveHappenedOnceExactly();

        // A reloaded CK cache has a new type collection -> recomputed.
        _types = [];
        AccessQueryGuard.GetHiddenAttributeNames(_ckCacheService, TenantId);
        A.CallTo(() => _ckCacheService.GetCkRecords(TenantId)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public void UnloadedTenant_FailsClosed()
    {
        A.CallTo(() => _ckCacheService.GetCkTypes("unloaded")).Throws(new InvalidOperationException("not loaded"));

        var act = () => AccessQueryGuard.GetHiddenAttributeNames(_ckCacheService, "unloaded");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("PasswordHash")]
    [InlineData("nav->passwordHash")]
    public void UnknownTarget_IsCheckedByName(string path)
    {
        var options = RtEntityQueryOptions.Create().FieldFilter(path, FieldFilterOperator.Equals, "x");

        var act = () => AccessQueryGuard.EnsureQueryOptionsAllowed(_ckCacheService, TenantId,
            new RtCkId<CkTypeId>("Unknown/Type"), options);

        act.Should().Throw<HiddenAttributeAccessException>().Which.Code.Should().Be("ATTRIBUTE_NOT_QUERYABLE");
    }

    [Fact]
    public void UnknownTarget_VisibleName_IsAllowed()
    {
        var options = RtEntityQueryOptions.Create().FieldFilter("name", FieldFilterOperator.Equals, "x")
            .SortOrder("name", SortOrders.Ascending);

        var act = () => AccessQueryGuard.EnsureQueryOptionsAllowed(_ckCacheService, TenantId,
            new RtCkId<CkTypeId>("Unknown/Type"), options);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("members.Test/Account[passwordHash=AQ]->name", true)]
    [InlineData("members.Test/Account[name=AQ]->name", false)]
    public void EntitySelectorKey_IsChecked(string path, bool hidden)
    {
        AccessQueryGuard.IsHiddenPath(_ckCacheService, TenantId, null, path).Should().Be(hidden);
    }

    [Fact]
    public void IsHiddenName_IsCaseInsensitiveOnTheStoredName()
    {
        AccessQueryGuard.IsHiddenName(_ckCacheService, TenantId, "passwordHash").Should().BeTrue();
        AccessQueryGuard.IsHiddenName(_ckCacheService, TenantId, "name").Should().BeFalse();
    }

    [Theory]
    [InlineData("credentials.token", true)]
    [InlineData("credentials", true)]
    [InlineData("label", false)]
    public void HiddenRecordValuedAttribute_HidesItsFields(string path, bool hidden)
    {
        // Re-review M7 gap: every segment is checked, not only the last one.
        var (outer, inner) = RecordWithHiddenRecordAttribute();
        CkRecordGraph? found = inner;
        A.CallTo(() => _ckCacheService.TryGetCkRecord(TenantId, inner.CkRecordId, out found)).Returns(true)
            .AssignsOutAndRefParameters(inner);

        AccessQueryGuard.IsHiddenPath(_ckCacheService, TenantId, outer, path).Should().Be(hidden);
    }

    [Fact]
    public void SecretSelectorKey_IsRejected()
    {
        var act = () => AccessQueryGuard.EnsureColumnPathsAllowed(_ckCacheService, TenantId,
            new RtCkId<CkTypeId>("Unknown/Type"), ["nav.testCredential[apiKey='x']->name"]);

        act.Should().Throw<Meshmakers.Octo.Runtime.Contracts.Secrets.SecretAttributeNotQueryableException>();
    }

    [Fact]
    public void HiddenSelectorKey_InColumnPath_IsRejected()
    {
        var act = () => AccessQueryGuard.EnsureColumnPathsAllowed(_ckCacheService, TenantId,
            new RtCkId<CkTypeId>("Unknown/Type"), ["name", "nav.testAccount[passwordHash='x']->name"]);

        act.Should().Throw<HiddenAttributeAccessException>().Which.Code.Should().Be("ATTRIBUTE_NOT_QUERYABLE");
    }

    [Fact]
    public void VisibleSelectorKey_InColumnPath_IsAllowed()
    {
        var act = () => AccessQueryGuard.EnsureColumnPathsAllowed(_ckCacheService, TenantId,
            new RtCkId<CkTypeId>("Unknown/Type"), ["name", "nav.testAccount[name='x']->name"]);

        act.Should().NotThrow();
    }

    private static (CkRecordGraph Outer, CkRecordGraph Inner) RecordWithHiddenRecordAttribute()
    {
        var token = Attribute("Token", AttributeValueTypesDto.String);
        var inner = new CkRecordGraph(new CkId<CkRecordId>("Test/Inner"), false, false, [], null, [], [],
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> { [token.CkAttributeId] = token }, "inner");
        var credentials = new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/Credentials"), "Credentials", null,
            AttributeValueTypesDto.Record, inner.CkRecordId, null, null, null, null, true, null)
        {
            Access = CkAttributeAccessDto.Hidden
        };
        var label = Attribute("Label", AttributeValueTypesDto.String);
        var outer = new CkRecordGraph(new CkId<CkRecordId>("Test/Outer"), false, false, [], null, [], [],
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
            {
                [credentials.CkAttributeId] = credentials,
                [label.CkAttributeId] = label
            }, "outer");
        return (outer, inner);
    }

    private static CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto valueType)
    {
        return new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/" + name), name, null, valueType, null, null,
            null, null, null, true, null);
    }

    private static CkRecordGraph HiddenRecord()
    {
        var hidden = new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/PasswordHash"), "PasswordHash", null,
            AttributeValueTypesDto.String, null, null, null, null, null, true, null) { Access = CkAttributeAccessDto.Hidden };
        var visible = new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/Name"), "Name", null,
            AttributeValueTypesDto.String, null, null, null, null, null, false, null);
        var secret = new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/ApiKey"), "ApiKey", null,
            AttributeValueTypesDto.Secret, null, null, null, null, null, true, null);
        return new CkRecordGraph(new CkId<CkRecordId>("Test/Credentials"), false, false, [], null, [], [],
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
            {
                [hidden.CkAttributeId] = hidden,
                [visible.CkAttributeId] = visible,
                [secret.CkAttributeId] = secret
            }, "test");
    }
}
