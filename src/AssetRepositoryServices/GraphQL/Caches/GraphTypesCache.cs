using System.Collections.Concurrent;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Implements the graph type cache
/// </summary>
internal class GraphTypesCache : IGraphTypesCache
{
    private readonly ICkCacheService _ckCacheService;
    private readonly IOctoService _octoService;
    private readonly ConcurrentDictionary<IGraphType, DynamicConnectionType> _connectionTypes;

    private readonly ConcurrentDictionary<CkId<CkEnumId>, RtEnumScalarType> _enumTypes;
    private readonly ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoInputType> _inputRecordTypes;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType> _inputTypes;

    private readonly ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoType> _recordTypes;
    private readonly string _tenantId;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoType> _types;
    private readonly ConcurrentDictionary<CkId<CkTypeId>, StreamDataEntityDtoType> _tsTypes;
    

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService"></param>
    /// <param name="tenantId"></param>
    /// <param name="ckCacheService"></param>
    public GraphTypesCache(ICkCacheService ckCacheService, IOctoService octoService, string tenantId)
    {
        _ckCacheService = ckCacheService;
        _octoService = octoService;
        _tenantId = tenantId;
        _enumTypes = new ConcurrentDictionary<CkId<CkEnumId>, RtEnumScalarType>();
        _types = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoType>();
        _inputTypes = new ConcurrentDictionary<CkId<CkTypeId>, RtEntityDtoInputType>();
        _recordTypes = new ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoType>();
        _inputRecordTypes = new ConcurrentDictionary<CkId<CkRecordId>, RtRecordDtoInputType>();
        _connectionTypes = new ConcurrentDictionary<IGraphType, DynamicConnectionType>();
        _tsTypes = new ConcurrentDictionary<CkId<CkTypeId>, StreamDataEntityDtoType>();
    }


    /// <inheritdoc />
    public DynamicConnectionType GetOrCreateConnection(IGraphType graphType)
    {
        var typeName = graphType.Name;
        return _connectionTypes.GetOrAdd(graphType, _ =>
        {
            var edgeType = new DynamicEdgeType(
                $"{typeName}{Statics.GraphQlEdgeSuffix}",
                $"An edge in a connection from an object to another object of type `{graphType.Name}`.", graphType);

            return new DynamicConnectionType
            (
                $"{typeName}{Statics.GraphQlConnectionSuffix}",
                $"A connection to `{typeName}`.",
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
    public StreamDataEntityDtoType[] GetStreamTypes()
    {
        return _tsTypes.Values.ToArray();
    }

    public RtEntityDtoType GetType(CkId<CkTypeId> ckTypeId)
    {
        return _types[ckTypeId];
    }

    public RtEntityDtoInputType GetInputType(CkId<CkTypeId> ckTypeId)
    {
        return _inputTypes[ckTypeId];
    }

    /// <inheritdoc />
    public RtRecordDtoType[] GetRecords()
    {
        // ReSharper disable once CoVariantArrayConversion
        return _recordTypes.Values.ToArray();
    }

    public RtRecordDtoType GetRecord(CkId<CkRecordId> ckRecordId)
    {
        return _recordTypes[ckRecordId];
    }

    public RtRecordDtoInputType GetRecordInput(CkId<CkRecordId> ckRecordId)
    {
        return _inputRecordTypes[ckRecordId];
    }

    public RtEnumScalarType GetEnum(CkId<CkEnumId> ckEnumId)
    {
        return _enumTypes[ckEnumId];
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

    public async Task PopulateAsync()
    {
        ITenantContext tenantContext = _octoService.SystemContext;
        if (_tenantId != _octoService.SystemContext.TenantId)
        {
            tenantContext = await _octoService.SystemContext.GetChildTenantContextAsync(_tenantId);
        }

        // The cache is normally created when a tenant repository is access first time. This will not work because
        // we need the schema now. So we have to load the cache manually.
        await tenantContext.LoadCacheForTenantAsync();

        // Create enum types first, because other elements depend on it.     
        foreach (var ckEnumGraph in _ckCacheService.GetCkEnums(_tenantId))
        {
            var rtEnumType = _enumTypes.GetOrAdd(ckEnumGraph.CkEnumId, _ =>
            {
                var rtEnumType = new RtEnumScalarType(ckEnumGraph.CkEnumId);
                return rtEnumType;
            });
            rtEnumType.Populate(_ckCacheService, _tenantId, this, ckEnumGraph);
        }

        // Make records second, because types depend on it.
        foreach (var ckRecordGraph in _ckCacheService.GetCkRecords(_tenantId))
        {
            _recordTypes.GetOrAdd(ckRecordGraph.CkRecordId, _ =>
            {
                var rtRecordDtoType = new RtRecordDtoType(ckRecordGraph.CkRecordId);
                return rtRecordDtoType;
            });
            
            if (!ckRecordGraph.IsAbstract)
            {
                 _inputRecordTypes.GetOrAdd(ckRecordGraph.CkRecordId, _ =>
                {
                    var rtRecordDtoInputType = new RtRecordDtoInputType(ckRecordGraph.CkRecordId);
                    return rtRecordDtoInputType;
                });
            }
        }

        foreach (var rtRecordDtoType in _recordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetCkRecord(_tenantId, rtRecordDtoType.CkRecordId);
            rtRecordDtoType.Populate(_ckCacheService, _tenantId, this, ckRecordGraph);
        }
        foreach (var rtRecordDtoInputType in _inputRecordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetCkRecord(_tenantId, rtRecordDtoInputType.CkRecordId);
            rtRecordDtoInputType.Populate(_ckCacheService, _tenantId, this, ckRecordGraph);
        }

        foreach (var ckTypeGraph in _ckCacheService.GetCkTypes(_tenantId))
        {
            _types.GetOrAdd(ckTypeGraph.CkTypeId, _ =>
            {
                var rtEntityType = new RtEntityDtoType(ckTypeGraph);
                return rtEntityType;
            });

            if (!ckTypeGraph.IsAbstract)
            {
                var rtEntityDtoInputType = _inputTypes.GetOrAdd(ckTypeGraph.CkTypeId, _ =>
                {
                    var rtEntityDtoInputType = new RtEntityDtoInputType(ckTypeGraph.CkTypeId);
                    return rtEntityDtoInputType;
                });
                rtEntityDtoInputType.Populate(_ckCacheService, _tenantId, this, ckTypeGraph);
            }

            if (ckTypeGraph.IsStreamType)
            {
                _ =_tsTypes.GetOrAdd(ckTypeGraph.CkTypeId, _ =>
                {
                    var tsEntityDtoType = new StreamDataEntityDtoType(ckTypeGraph);
                    return tsEntityDtoType;
                });
            }
        }

        foreach (var rtEntityDtoType in _types.Values)
        {
            rtEntityDtoType.Populate(_ckCacheService, _tenantId, this);
        }

        foreach (var tsEntityDtoType in _tsTypes.Values)
        {
            tsEntityDtoType.Populate(this);
        }
    }
}