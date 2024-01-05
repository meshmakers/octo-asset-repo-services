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
    private readonly ICkCacheService _ckCacheService;
    private readonly ConcurrentDictionary<IGraphType, DynamicConnectionType> _connectionTypes;
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;

    private readonly ConcurrentDictionary<CkId<CkEnumId>, RtEnumScalarType> _enumTypes;
    private readonly ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoInputType> _inputRecordTypes;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType> _inputTypes;
    private readonly IOctoSessionAccessor _octoSessionAccessor;

    private readonly ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoType> _recordTypes;
    private readonly string _tenantId;
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
        _enumTypes = new ConcurrentDictionary<CkId<CkEnumId>, RtEnumScalarType>();
        _types = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoType>();
        _inputTypes = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType>();
        _recordTypes = new ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoType>();
        _inputRecordTypes = new ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoInputType>();
        _connectionTypes = new ConcurrentDictionary<IGraphType, DynamicConnectionType>();
    }

    /// <inheritdoc />
    public RtEntityDtoType GetOrCreate(CkId<CkTypeId> ckId)
    {
        return _types.GetOrAdd(ckId, _ =>
        {
            var rtEntityType = new RtEntityDtoType(ckId);
            return rtEntityType;
        });
    }

    /// <inheritdoc />
    public RtEntityDtoInputType GetOrCreateInput(CkId<CkTypeId> ckId)
    {
        return _inputTypes.GetOrAdd(ckId, _ =>
        {
            var rtEntityDtoInputType = new RtEntityDtoInputType(ckId);
            return rtEntityDtoInputType;
        });
    }

    /// <inheritdoc />
    public RtRecordDtoType GetOrCreate(CkId<CkRecordId> ckId)
    {
        return _recordTypes.GetOrAdd(ckId, _ =>
        {
            var rtRecordDtoType = new RtRecordDtoType(ckId);
            return rtRecordDtoType;
        });
    }

    /// <inheritdoc />
    public RtRecordDtoInputType GetOrCreateInput(CkId<CkRecordId> ckId)
    {
        return _inputRecordTypes.GetOrAdd(ckId, _ =>
        {
            var rtRecordDtoInputType = new RtRecordDtoInputType(ckId);
            return rtRecordDtoInputType;
        });
    }

    /// <inheritdoc />
    public RtEnumScalarType GetOrCreate(CkId<CkEnumId> ckId)
    {
        return _enumTypes.GetOrAdd(ckId, _ =>
        {
            var rtEnumType = new RtEnumScalarType(ckId);
            return rtEnumType;
        });
    }


    /// <inheritdoc />
    public DynamicConnectionType GetOrCreateConnection(IGraphType graphType, string prefixName)
    {
        return _connectionTypes.GetOrAdd(graphType, _ =>
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
    public RtEntityDtoType[] GetTypes()
    {
        // ReSharper disable once CoVariantArrayConversion
        return _types.Values.ToArray();
    }

    /// <inheritdoc />
    public RtRecordDtoType[] GetRecords()
    {
        // ReSharper disable once CoVariantArrayConversion
        return _recordTypes.Values.ToArray();
    }

    /// <inheritdoc />
    public IGraphType[] GetKnownGraphTypes()
    {
        var inputTypes = new List<IGraphType>();
        inputTypes.AddRange(_types.Values);
        inputTypes.AddRange(_inputTypes.Values);
        inputTypes.AddRange(_enumTypes.Values);
        inputTypes.AddRange(_recordTypes.Values);
        inputTypes.AddRange(_inputRecordTypes.Values);
        return inputTypes.ToArray();
    }

    public void Populate()
    {
        foreach (var rtEntityDtoType in _types.Values)
        {
            var ckTypeGraph = _ckCacheService.GetCkType(_tenantId, rtEntityDtoType.CkTypeId);
            rtEntityDtoType.Populate(_ckCacheService, _tenantId, this, _dataLoaderAccessor, _octoSessionAccessor, ckTypeGraph);
        }

        foreach (var rtRecordDtoType in _recordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetCkRecord(_tenantId, rtRecordDtoType.CkRecordId);
            rtRecordDtoType.Populate(_ckCacheService, _tenantId, this, _dataLoaderAccessor, _octoSessionAccessor, ckRecordGraph);
        }

        foreach (var rtEnumType in _enumTypes.Values)
        {
            var ckEnumGraph = _ckCacheService.GetCkEnum(_tenantId, rtEnumType.CkEnumId);
            rtEnumType.Populate(_ckCacheService, _tenantId, this, _dataLoaderAccessor, _octoSessionAccessor, ckEnumGraph);
        }

        foreach (var rtEntityDtoInputType in _inputTypes.Values)
        {
            var ckTypeGraph = _ckCacheService.GetCkType(_tenantId, rtEntityDtoInputType.CkTypeId);
            rtEntityDtoInputType.Populate(_ckCacheService, _tenantId, this, ckTypeGraph);
        }

        foreach (var rtRecordDtoInputType in _inputRecordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetCkRecord(_tenantId, rtRecordDtoInputType.CkRecordId);
            rtRecordDtoInputType.Populate(_ckCacheService, _tenantId, this, ckRecordGraph);
        }
    }
}