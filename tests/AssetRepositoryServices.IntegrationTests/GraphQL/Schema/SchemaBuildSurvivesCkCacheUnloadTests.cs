using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
///     AB#8998: a <c>PreUpdateTenant</c> (CK import, revalidation) unloads the tenant's CK cache in every service. When that
///     happens while the GraphQL schema is built - after the cache was loaded for the build and before the schema is
///     initialized - the build failed with "CkCache for tenant 'x' not found" and the request ended in an HTTP 500 (prod-2,
///     tenant sbeg, 2026-11-15). AB#8667 covered a reload, not an unload. The build is now retried once from a fresh load.
/// </summary>
[Collection(GraphQlCollection.Name)]
public class SchemaBuildSurvivesCkCacheUnloadTests
{
    private readonly GraphQlTestFixture _fixture;

    public SchemaBuildSurvivesCkCacheUnloadTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task CkCacheUnloadedBetweenLoadAndInitialize_RetriesOnceAndBuildsTheSchema()
    {
        var (schemaContext, ckCache, tenantId) = Arrange();
        var attempts = new List<int>();
        schemaContext.AfterPopulateForTesting = (tenant, attempt) =>
        {
            attempts.Add(attempt);
            if (attempt == 1)
            {
                // What PreUpdatePreDeleteTenantConsumer does on another thread while the schema is built.
                ckCache.Unload(tenant);
            }

            return Task.CompletedTask;
        };

        try
        {
            var schema = await schemaContext.GetOrCreateAsync(tenantId);

            schema.Initialized.Should().BeTrue();
            attempts.Should().Equal([1, 2], "the build is retried exactly once from a fresh load");
            ckCache.IsTenantLoaded(tenantId).Should().BeTrue("the retry loads the CK cache again");
        }
        finally
        {
            schemaContext.AfterPopulateForTesting = null;
        }

        (await schemaContext.GetOrCreateAsync(tenantId)).Should().NotBeNull();
    }

    [Fact]
    public async Task CkCacheUnloadedOnBothAttempts_FailsAfterOneRetry()
    {
        var (schemaContext, ckCache, tenantId) = Arrange();
        var attempts = new List<int>();
        schemaContext.AfterPopulateForTesting = (tenant, attempt) =>
        {
            attempts.Add(attempt);
            ckCache.Unload(tenant);
            return Task.CompletedTask;
        };

        try
        {
            var act = () => schemaContext.GetOrCreateAsync(tenantId);

            await act.Should().ThrowAsync<OctoGraphQLException>();
            attempts.Should().Equal([1, 2], "the retry is bounded to one");
        }
        finally
        {
            schemaContext.AfterPopulateForTesting = null;
        }

        (await schemaContext.GetOrCreateAsync(tenantId)).Initialized.Should().BeTrue(
            "the next request builds the schema again");
    }

    [Fact]
    public async Task CkCacheStableDuringBuild_BuildsOnce()
    {
        var (schemaContext, _, tenantId) = Arrange();
        var attempts = new List<int>();
        schemaContext.AfterPopulateForTesting = (_, attempt) =>
        {
            attempts.Add(attempt);
            return Task.CompletedTask;
        };

        try
        {
            (await schemaContext.GetOrCreateAsync(tenantId)).Initialized.Should().BeTrue();
            attempts.Should().Equal([1]);
        }
        finally
        {
            schemaContext.AfterPopulateForTesting = null;
        }
    }

    private (SchemaContext SchemaContext, ICkCacheService CkCache, string TenantId) Arrange()
    {
        var schemaContext = (SchemaContext)_fixture.GetService<ISchemaContext>();
        var tenantId = _fixture.GetSystemContext().TenantId;

        // Start from no cached schema, so GetOrCreateAsync builds one.
        schemaContext.Invalidate(tenantId);
        return (schemaContext, _fixture.GetService<ICkCacheService>(), tenantId);
    }
}
