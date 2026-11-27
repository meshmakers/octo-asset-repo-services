using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

public class SecretQueryGuardTypeLookupTests
{
    private const string TenantId = "guardtenant";

    [Theory]
    [InlineData(null)]
    [InlineData("Test/Unknown-1")]
    public void MissingOrUnknownType_PassesTheGuardWithoutAnError(string? ckTypeIdText)
    {
        var ckCacheService = CkCacheWithoutTypes();
        var ckTypeId = ckTypeIdText is null ? null : new RtCkId<CkTypeId>(ckTypeIdText);

        var ensureQueryable = () => SecretQueryGuard.EnsureQueryable(ckCacheService, TenantId, ckTypeId,
            RtEntityQueryOptions.Create());

        ensureQueryable.Should().NotThrow();
    }

    private static ICkCacheService CkCacheWithoutTypes()
    {
        var ckCacheService = A.Fake<ICkCacheService>();
        CkTypeGraph? noType;
        A.CallTo(() => ckCacheService.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out noType)).Returns(false);
        A.CallTo(() => ckCacheService.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>.That.IsNull(), out noType))
            .Throws(new ArgumentNullException("key"));
        return ckCacheService;
    }
}
