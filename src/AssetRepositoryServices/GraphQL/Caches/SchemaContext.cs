using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
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
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SchemaContext(IOptions<OctoAssetRepositoryServicesOptions> options,
        IDistributedWithPubSubCache distributedWithPubSubCache,
        IDataLoaderContextAccessor dataLoaderAccessor, IOctoSessionAccessor octoSessionAccessor)
    {
        _options = options;
        _dataLoaderAccessor = dataLoaderAccessor;
        _octoSessionAccessor = octoSessionAccessor;

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 64
        });

        var sub = distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        sub.OnMessage(message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                _cache.Remove(message.Message.MakeKey());
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     Invalidates a cached schema
    /// </summary>
    /// <param name="tenantId">The Id of tenant</param>
    public void Invalidate(string tenantId)
    {
        var key = tenantId.MakeKey();
        _cache.Remove(key);
    }

    /// <inheritdoc />
    public async Task<ISchema> GetOrCreateAsync(ITenantContext tenantContext)
    {
        var key = tenantContext.TenantId.MakeKey();

        Logger.Debug($"Looking up GraphQL schema for {tenantContext.TenantId}");

        if (!_cache.TryGetValue(key, out OctoSchema? schema))
        {
            try
            {
                await _semaphore.WaitAsync();

                var t = new Func<ICacheEntry, OctoSchema>(entry =>
                {
                    Logger.Debug($"Creating GraphQL schema for {tenantContext.TenantId}");
                    entry.SetSize(1);
                    entry.SlidingExpiration = TimeSpan.FromDays(1);

                    var graphTypesCache = new GraphTypesCache(tenantContext, _dataLoaderAccessor, _octoSessionAccessor);
                    var ckEntities = tenantContext.CkCache.GetCkEntities().Where(x => !x.IsAbstract).ToList();
                    var rtEntitiesTypes = ckEntities.Select(ck => graphTypesCache.GetOrCreate(ck.CkId)).ToList();

                    var query = new OctoQuery(_options, graphTypesCache, _dataLoaderAccessor, _octoSessionAccessor,
                        rtEntitiesTypes);
                    var mutation = new OctoMutation(ckEntities, graphTypesCache, _octoSessionAccessor,
                        tenantContext.CkCache);
                    var subscriptions = new OctoSubscriptions(rtEntitiesTypes);

                    graphTypesCache.Populate();

                    var createdSchema = new OctoSchema(query, mutation, subscriptions);
                    createdSchema.RegisterTypes(graphTypesCache.GetTypes());

                    Logger.Debug($"GraphQL schema for {tenantContext.TenantId} completed");
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
