using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>AB#8667: the marker that ties a tenant's GraphQL schema to one load of its CK cache.</summary>
public class CkCacheGenerationTests
{
    private const string TenantId = "meshtest";

    [Fact]
    public void SameLoadedCache_IsNotChanged()
    {
        var cache = A.Fake<ICkCacheService>();
        var types = new List<CkTypeGraph>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).Returns(true);
        A.CallTo(() => cache.GetCkTypes(TenantId)).Returns(types);

        var marker = CkCacheGeneration.Capture(cache, TenantId);

        marker.Should().BeSameAs(types);
        CkCacheGeneration.HasChanged(cache, TenantId, marker).Should().BeFalse();
    }

    [Fact]
    public void ReloadedCache_IsChanged()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).Returns(true);
        A.CallTo(() => cache.GetCkTypes(TenantId)).ReturnsNextFromSequence(new List<CkTypeGraph>(), new List<CkTypeGraph>());

        var marker = CkCacheGeneration.Capture(cache, TenantId);

        CkCacheGeneration.HasChanged(cache, TenantId, marker).Should().BeTrue();
    }

    [Fact]
    public void UnloadedCache_IsChanged_AndHasNoMarker()
    {
        var cache = A.Fake<ICkCacheService>();
        var types = new List<CkTypeGraph>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).ReturnsNextFromSequence(true, false, false);
        A.CallTo(() => cache.GetCkTypes(TenantId)).Returns(types);

        var marker = CkCacheGeneration.Capture(cache, TenantId);

        CkCacheGeneration.HasChanged(cache, TenantId, marker).Should().BeTrue();
        CkCacheGeneration.Capture(cache, TenantId).Should().BeNull();
    }

    [Fact]
    public void CacheUnloadedBetweenTheChecks_HasNoMarker_AndASchemaWithoutMarkerIsStale()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).Returns(true);
        A.CallTo(() => cache.GetCkTypes(TenantId)).Throws(new CkCacheException("unloaded"));

        CkCacheGeneration.Capture(cache, TenantId).Should().BeNull();
        CkCacheGeneration.HasChanged(cache, TenantId, builtFrom: null).Should().BeTrue();
    }
}
