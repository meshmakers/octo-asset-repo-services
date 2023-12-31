using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     The schema context allows to cache GraphQL Schemas based on a data source
/// </summary>
internal class SchemaContext : ISchemaContext
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache;
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;
    private readonly IOctoSessionAccessor _octoSessionAccessor;

    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;
    private readonly ICkCacheService _ckCacheService;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SchemaContext(IOptions<OctoAssetRepositoryServicesOptions> options,
        ICkCacheService ckCacheService,
        IDataLoaderContextAccessor dataLoaderAccessor, IOctoSessionAccessor octoSessionAccessor)
    {
        _options = options;
        _ckCacheService = ckCacheService;
        _dataLoaderAccessor = dataLoaderAccessor;
        _octoSessionAccessor = octoSessionAccessor;

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 64
        });

        // TODO: Add again.
        // var sub = distributedCache.SubscribeEvent<string>(CacheCommon.KeyTenantPreUpdate);
        // sub.OnEvent(tenantId =>
        // {
        //     if (!string.IsNullOrWhiteSpace(tenantId))
        //     {
        //         _cache.Remove(tenantId.MakeKey());
        //     }
        //
        //     return Task.CompletedTask;
        // });
    }

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

        Logger.Debug($"Looking up GraphQL schema for {tenantId}");

        if (!_cache.TryGetValue(key, out OctoSchema? schema))
        {
            try
            {
                await _semaphore.WaitAsync();

                var t = new Func<ICacheEntry, OctoSchema>(entry =>
                {
                    Logger.Debug($"Creating GraphQL schema for {tenantId}");
                    entry.SetSize(1);
                    entry.SlidingExpiration = TimeSpan.FromDays(1);

                    var graphTypesCache = new GraphTypesCache(_ckCacheService, tenantId, _dataLoaderAccessor, _octoSessionAccessor);
                    
                    var ckEntities = _ckCacheService.GetCkTypes(tenantId).Where(x => !x.IsAbstract).ToList();
                    var rtEntitiesTypes = ckEntities.Select(ck => graphTypesCache.GetOrCreate(ck.CkTypeId)).ToList();

                    var query = new OctoQuery(_options, graphTypesCache, _dataLoaderAccessor, _octoSessionAccessor,
                        rtEntitiesTypes);
                    var mutation = new OctoMutation(ckEntities, graphTypesCache, _octoSessionAccessor);
                    var subscriptions = new OctoSubscriptions(rtEntitiesTypes);

                    graphTypesCache.Populate();

                    var createdSchema = new OctoSchema(query, mutation, subscriptions);
                    createdSchema.RegisterTypes(graphTypesCache.GetTypes());

                    Logger.Debug($"GraphQL schema for {tenantId} completed");
                    return createdSchema;
                });

                return _cache.GetOrCreate(key, t)!;
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        return schema!;
    }
}
