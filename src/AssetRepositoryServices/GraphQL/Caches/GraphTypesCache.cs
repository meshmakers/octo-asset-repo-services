using System.Collections.Concurrent;
using System.Linq;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Implements the graph type cache
/// </summary>
internal class GraphTypesCache : IGraphTypesCache
{
    private readonly ConcurrentDictionary<IGraphType, DynamicConnectionType> _connectionTypes;
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;
    private readonly ConcurrentDictionary<string, RtEntityDtoInputType> _inputTypes;
    private readonly IOctoSessionAccessor _octoSessionAccessor;
    private readonly ITenantContext _tenantContext;
    private readonly ConcurrentDictionary<string, RtEntityDtoType> _types;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantContext"></param>
    /// <param name="dataLoaderAccessor">Data loader context accessor to solve the n+1 issue</param>
    public GraphTypesCache(ITenantContext tenantContext, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor octoSessionAccessor)
    {
        _tenantContext = tenantContext;
        _dataLoaderAccessor = dataLoaderAccessor;
        _octoSessionAccessor = octoSessionAccessor;
        _types = new ConcurrentDictionary<string, RtEntityDtoType>();
        _inputTypes = new ConcurrentDictionary<string, RtEntityDtoInputType>();
        _connectionTypes = new ConcurrentDictionary<IGraphType, DynamicConnectionType>();
    }

    /// <inheritdoc />
    public RtEntityDtoType GetOrCreate(string ckId)
    {
        return _types.GetOrAdd(ckId, s =>
        {
            var rtEntityType = new RtEntityDtoType(ckId);
            return rtEntityType;
        });
    }

    /// <inheritdoc />
    public RtEntityDtoInputType GetOrCreateInput(string ckId)
    {
        return _inputTypes.GetOrAdd(ckId, s =>
        {
            var rtEntityDtoInputType = new RtEntityDtoInputType(ckId);
            return rtEntityDtoInputType;
        });
    }

    /// <inheritdoc />
    public DynamicConnectionType GetOrCreateConnection(IGraphType graphType, string prefixName)
    {
        return _connectionTypes.GetOrAdd(graphType, s =>
        {
            var edgeType = new DynamicEdgeType(
                $"{prefixName}{CommonConstants.GraphQlEdgeSuffix}",
                $"An edge in a connection from an object to another object of type `{graphType.Name}`.", graphType);

            return new DynamicConnectionType
            (
                $"{prefixName}{CommonConstants.GraphQlConnectionSuffix}",
                $"A connection to `{prefixName}`.",
                graphType, edgeType
            );
        });
    }

    /// <inheritdoc />
    public IGraphType[] GetTypes()
    {
        // ReSharper disable once CoVariantArrayConversion
        return _types.Values.ToArray();
    }

    public void Populate()
    {
        foreach (var rtEntityDtoType in _types.Values)
        {
            var entityCacheItem = _tenantContext.CkCache.GetEntityCacheItem(rtEntityDtoType.CkId);
            rtEntityDtoType.Populate(this, _dataLoaderAccessor, _octoSessionAccessor, entityCacheItem);
        }

        foreach (var rtEntityDtoInputType in _inputTypes.Values)
        {
            var entityCacheItem = _tenantContext.CkCache.GetEntityCacheItem(rtEntityDtoInputType.CkId);
            rtEntityDtoInputType.Populate(entityCacheItem);
        }
    }
}
