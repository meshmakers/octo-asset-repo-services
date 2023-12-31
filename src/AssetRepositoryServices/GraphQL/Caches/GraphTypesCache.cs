using System.Collections.Concurrent;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Implements the graph type cache
/// </summary>
internal class GraphTypesCache : IGraphTypesCache
{
    private readonly ConcurrentDictionary<IGraphType, DynamicConnectionType> _connectionTypes;
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType> _inputTypes;
    private readonly IOctoSessionAccessor _octoSessionAccessor;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoType> _types;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="dataLoaderAccessor">Data loader context accessor to solve the n+1 issue</param>
    /// <param name="ckCacheService"></param>
    /// <param name="octoSessionAccessor"></param>
    public GraphTypesCache(ICkCacheService ckCacheService, string tenantId, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor octoSessionAccessor)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _dataLoaderAccessor = dataLoaderAccessor;
        _octoSessionAccessor = octoSessionAccessor;
        _types = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoType>();
        _inputTypes = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType>();
        _connectionTypes = new ConcurrentDictionary<IGraphType, DynamicConnectionType>();
    }

    /// <inheritdoc />
    public RtEntityDtoType GetOrCreate(CkId<CkTypeId> ckId)
    {
        return _types.GetOrAdd(ckId, s =>
        {
            var rtEntityType = new RtEntityDtoType(ckId);
            return rtEntityType;
        });
    }

    /// <inheritdoc />
    public RtEntityDtoInputType GetOrCreateInput(CkId<CkTypeId> ckId)
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
            var entityCacheItem = _ckCacheService.GetCkType(_tenantId, rtEntityDtoType.CkTypeId);
            rtEntityDtoType.Populate(_ckCacheService, _tenantId, this, _dataLoaderAccessor, _octoSessionAccessor, entityCacheItem);
        }

        foreach (var rtEntityDtoInputType in _inputTypes.Values)
        {
            var entityCacheItem = _ckCacheService.GetCkType(_tenantId, rtEntityDtoInputType.CkTypeId);
            rtEntityDtoInputType.Populate(_ckCacheService, _tenantId, entityCacheItem);
        }
    }
}
