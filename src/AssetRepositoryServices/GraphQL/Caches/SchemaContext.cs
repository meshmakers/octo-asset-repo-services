using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    ILoggerFactory loggerFactory,
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

    private bool TryGetCurrent(string key, string tenantId, [NotNullWhen(true)] out ISchema? schema)
    {
        if (_cache.TryGetValue(key, out CachedSchema? cached) && cached != null
            && !CkCacheGeneration.HasChanged(ckCacheService, tenantId, cached.CkCacheMarker))
        {
            schema = cached.Schema;
            return true;
        }

        schema = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<ISchema> GetOrCreateAsync(string tenantId)
    {
        var key = tenantId.NormalizeString();

        logger.LogDebug("Looking up GraphQL schema for {TenantId}", tenantId);

        if (TryGetCurrent(key, tenantId, out var current))
        {
            return current;
        }

        try
        {
            await _semaphore.WaitAsync();

            // AB#8667: a schema is only as current as the CK cache it was built from. Every path that reloads the
            // CK cache (the AB#5415 stale-type fallback, the display-rule sweeper, an import, a PosUpdateTenant that
            // arrived) replaces the loaded model graph; a schema of an earlier load is dropped and built again here,
            // whether or not a PosUpdateTenant reached this process.
            if (TryGetCurrent(key, tenantId, out current))
            {
                return current;
            }

            if (_cache.TryGetValue(key, out CachedSchema? stale) && stale != null)
            {
                logger.LogInformation(
                    "GraphQL schema of tenant {TenantId} was built from an earlier load of the CK cache; rebuilding it (AB#8667)",
                    tenantId);
                _cache.Remove(key);
            }

            var t = new Func<ICacheEntry, Task<CachedSchema?>>(async entry =>
            {
                logger.LogDebug("Creating GraphQL schema for {TenantId}", tenantId);
                entry.SetSize(1);
                entry.SlidingExpiration = TimeSpan.FromDays(1);

                // Schema-build timing (CK v2 risk R8): the CK v2 types (interfaces, inheritance, association
                // members) must not make the per-tenant schema build noticeably slower.
                var stopwatch = Stopwatch.StartNew();

                var graphTypesCache = new GraphTypesCache(ckCacheService, octoService, options, tenantId,
                    loggerFactory.CreateLogger<GraphTypesCache>());
                await graphTypesCache.PopulateAsync();
                var populateMs = stopwatch.ElapsedMilliseconds;

                var query = new OctoQuery(loggerFactory, graphTypesCache);
                var mutation = new OctoMutation(loggerFactory, graphTypesCache);
                var subscriptions = new OctoSubscriptions(graphTypesCache);

                var createdSchema = new OctoSchema(serviceProvider, query, mutation, subscriptions);
                createdSchema.RegisterTypes(graphTypesCache.GetKnownGraphTypes());

                // Initialize eagerly so the measured time is the real build cost (GraphQL.NET would otherwise
                // initialize lazily on the first request) and an invalid schema is never cached.
                createdSchema.Initialize();
                stopwatch.Stop();

                var statistics = graphTypesCache.GetStatistics();
                logger.LogInformation(
                    "GraphQL schema for tenant {TenantId} built in {ElapsedMs} ms ({TypeCount} types, {InterfaceCount} CK interfaces, {MethodCount} CK methods; populate {PopulateMs} ms, initialize {InitializeMs} ms)",
                    tenantId, stopwatch.ElapsedMilliseconds, createdSchema.AllTypes.Count, statistics.CkInterfaceCount,
                    statistics.MethodCount, populateMs, stopwatch.ElapsedMilliseconds - populateMs);

                return new CachedSchema(createdSchema, graphTypesCache.CkCacheMarker);
            });

            var returnSchema = await _cache.GetOrCreateAsync(key, t);
            if (returnSchema == null)
            {
                throw OctoGraphQLException.SchemaCreationFailed(tenantId);
            }

            return returnSchema.Schema;
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

/// <summary>A cached tenant schema and the CK cache load it was built from (AB#8667).</summary>
internal sealed record CachedSchema(ISchema Schema, object? CkCacheMarker);
