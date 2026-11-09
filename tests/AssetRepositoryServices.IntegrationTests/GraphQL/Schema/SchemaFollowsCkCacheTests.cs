using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
///     AB#8667: the tenant's GraphQL schema follows its CK cache. After a service restart the cache could be loaded
///     before another service's CK import and the <c>PosUpdateTenant</c> of that import was published before this
///     process listened to the bus; the AB#5415 fallback later reloaded the CK cache, but the schema stayed the one built
///     from the old cache (no archive types) until a manual ClearCache. A reload of the CK cache without any tenant event
///     must give a new schema, and an unchanged cache must keep the cached one (no extra cost).
/// </summary>
[Collection(GraphQlCollection.Name)]
public class SchemaFollowsCkCacheTests
{
    private readonly GraphQlTestFixture _fixture;

    public SchemaFollowsCkCacheTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task UnchangedCkCache_KeepsTheCachedSchema()
    {
        var (schemaContext, tenantId) = (_fixture.GetService<ISchemaContext>(), _fixture.GetSystemContext().TenantId);
        var schema = await schemaContext.GetOrCreateAsync(tenantId);

        (await schemaContext.GetOrCreateAsync(tenantId)).Should().BeSameAs(schema);
        (await schemaContext.GetOrCreateAsync(tenantId)).Should().BeSameAs(schema);
    }

    [Fact]
    public async Task CkCacheReloadedWithoutATenantEvent_RebuildsTheSchema()
    {
        var (schemaContext, tenantId) = (_fixture.GetService<ISchemaContext>(), _fixture.GetSystemContext().TenantId);
        var ckCache = _fixture.GetService<ICkCacheService>();
        var before = await schemaContext.GetOrCreateAsync(tenantId);

        // What the AB#5415 fallback (and the display-rule sweeper) does: unload and load again, no PosUpdateTenant.
        await ReloadCkCacheAsync(ckCache, tenantId);

        var after = await schemaContext.GetOrCreateAsync(tenantId);
        after.Should().NotBeSameAs(before, "the schema must be built from the CK cache that is loaded now");
        after.Initialized.Should().BeTrue();
        (await schemaContext.GetOrCreateAsync(tenantId)).Should().BeSameAs(after,
            "once rebuilt, the schema is cached again for the reloaded CK cache");
    }

    [Fact]
    public async Task CkCacheUnloadedWithoutATenantEvent_RebuildsTheSchemaFromAFreshLoad()
    {
        var (schemaContext, tenantId) = (_fixture.GetService<ISchemaContext>(), _fixture.GetSystemContext().TenantId);
        var ckCache = _fixture.GetService<ICkCacheService>();
        var before = await schemaContext.GetOrCreateAsync(tenantId);

        // An import of this or another service unloads the cache; the event that used to drop the schema may be lost.
        ckCache.Unload(tenantId);

        var after = await schemaContext.GetOrCreateAsync(tenantId);
        after.Should().NotBeSameAs(before);
        ckCache.IsTenantLoaded(tenantId).Should().BeTrue("building the schema loads the CK cache again");
    }

    private async Task ReloadCkCacheAsync(ICkCacheService ckCache, string tenantId)
    {
        if (ckCache.IsTenantLoaded(tenantId))
        {
            ckCache.Unload(tenantId);
        }

        await _fixture.GetSystemContext().LoadCacheForTenantAsync();
    }
}
