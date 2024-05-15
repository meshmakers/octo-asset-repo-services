using GraphQL;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     The schema context allows to cache GraphQL Schemas based on a data source
/// </summary>
internal class SchemaContext(
    ILogger<SchemaContext> logger,
    IServiceProvider serviceProvider,
    IOptions<OctoAssetRepositoryServicesOptions> options,
    ICkCacheService ckCacheService,
    IOctoService octoService)
    : ISchemaContext
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 64
    });

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    ///     Invalidates a cached schema
    /// </summary>
    /// <param name="tenantId">The Id of tenant</param>
    public void Invalidate(string tenantId)
    {
        var key = tenantId.NormalizeString();
        _cache.Remove(key);
    }

    /// <inheritdoc />
    public async Task<ISchema> GetOrCreateAsync(string tenantId)
    {
        var key = tenantId.NormalizeString();

        logger.LogDebug("Looking up GraphQL schema for {TenantId}", tenantId);

        if (_cache.TryGetValue(key, out OctoSchema? schema) && schema != null)
        {
            return schema;
        }

        try
        {
            await _semaphore.WaitAsync();

            var t = new Func<ICacheEntry, Task<ISchema?>>(async entry =>
            {
                logger.LogDebug("Creating GraphQL schema for {TenantId}", tenantId);    
                entry.SetSize(1);
                entry.SlidingExpiration = TimeSpan.FromDays(1);

                var graphTypesCache = new GraphTypesCache(ckCacheService, octoService, tenantId);
                await graphTypesCache.PopulateAsync();

                var query = new OctoQuery(options, graphTypesCache);
                var mutation = new OctoMutation(graphTypesCache);
                var subscriptions = new OctoSubscriptions(graphTypesCache);

                var createdSchema = new OctoSchema(serviceProvider, query, mutation, subscriptions);
                createdSchema.RegisterTypes(graphTypesCache.GetKnownGraphTypes());

                logger.LogDebug("GraphQL schema for {TenantId} completed", tenantId);
                return createdSchema;
            });

            var returnSchema = await _cache.GetOrCreateAsync(key, t);
            if (returnSchema == null)
            {
                throw OctoGraphQLException.SchemaCreationFailed(tenantId);
            }

            return returnSchema;
        }
        catch (Exception e)
        {
            throw OctoGraphQLException.SchemaCreationFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}