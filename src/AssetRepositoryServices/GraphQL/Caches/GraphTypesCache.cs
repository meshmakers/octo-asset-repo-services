using System.Collections.Concurrent;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Implements the graph type cache
/// </summary>
internal class GraphTypesCache : IGraphTypesCache
{
    private readonly ICkCacheService _ckCacheService;
    private readonly ConcurrentDictionary<IGraphType, DynamicConnectionType> _connectionTypes;

    private readonly ConcurrentDictionary<RtCkId<CkEnumId>, RtEnumScalarType> _enumTypes;
    private readonly ConcurrentDictionary<RtCkId<CkRecordId>, RtRecordDtoInputType> _inputRecordTypes;
    private readonly ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityDtoInputType> _inputTypes;
    private readonly ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityInterfaceType> _interfaceTypes;
    private readonly IOctoService _octoService;
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;

    private readonly ConcurrentDictionary<RtCkId<CkRecordId>, RtRecordDtoType> _recordTypes;
    private readonly string _tenantId;
    private readonly ConcurrentDictionary<RtCkId<CkTypeId>, StreamDataEntityDtoType> _tsTypes;
    private readonly ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityDtoType> _types;
    private readonly ConcurrentDictionary<(RtCkId<CkTypeId>, string, string), DynamicConnectionType> _interfaceAssociationConnections;


    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService"></param>
    /// <param name="options"></param>
    /// <param name="tenantId"></param>
    /// <param name="ckCacheService"></param>
    public GraphTypesCache(ICkCacheService ckCacheService, IOctoService octoService,
        IOptions<OctoAssetRepositoryServicesOptions> options, string tenantId)
    {
        _ckCacheService = ckCacheService;
        _octoService = octoService;
        _options = options;
        _tenantId = tenantId;
        _enumTypes = new ConcurrentDictionary<RtCkId<CkEnumId>, RtEnumScalarType>();
        _types = new ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityDtoType>();
        _inputTypes = new ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityDtoInputType>();
        _interfaceTypes = new ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityInterfaceType>();
        _recordTypes = new ConcurrentDictionary<RtCkId<CkRecordId>, RtRecordDtoType>();
        _inputRecordTypes = new ConcurrentDictionary<RtCkId<CkRecordId>, RtRecordDtoInputType>();
        _connectionTypes = new ConcurrentDictionary<IGraphType, DynamicConnectionType>();
        _tsTypes = new ConcurrentDictionary<RtCkId<CkTypeId>, StreamDataEntityDtoType>();
        _interfaceAssociationConnections = new ConcurrentDictionary<(RtCkId<CkTypeId>, string, string), DynamicConnectionType>();
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

    public RtEntityDtoType GetType(RtCkId<CkTypeId> ckTypeId)
    {
        return _types[ckTypeId];
    }

    /// <inheritdoc />
    public IReadOnlyList<RtEntityInterfaceType> GetImplementedInterfaces(RtCkId<CkTypeId> ckTypeId)
    {
        var interfaces = new List<RtEntityInterfaceType>();
        var ckTypeGraph = _ckCacheService.GetRtCkType(_tenantId, ckTypeId);

        // Walk through all base types and collect abstract ones as interfaces
        foreach (var baseType in ckTypeGraph.BaseTypes)
        {
            var baseCkTypeGraph = _ckCacheService.GetCkType(_tenantId, baseType.BaseCkTypeId);
            if (baseCkTypeGraph.IsAbstract)
            {
                var rtCkId = baseType.BaseCkTypeId.ToRtCkId();
                if (_interfaceTypes.TryGetValue(rtCkId, out var interfaceType))
                {
                    interfaces.Add(interfaceType);
                }
            }
        }

        return interfaces;
    }

    public RtEntityDtoInputType GetInputType(RtCkId<CkTypeId> ckTypeId)
    {
        return _inputTypes[ckTypeId];
    }

    /// <inheritdoc />
    public RtRecordDtoType[] GetRecords()
    {
        // ReSharper disable once CoVariantArrayConversion
        return _recordTypes.Values.ToArray();
    }

    public RtRecordDtoType GetRecord(RtCkId<CkRecordId> ckRecordId)
    {
        return _recordTypes[ckRecordId];
    }

    public RtRecordDtoInputType GetRecordInput(RtCkId<CkRecordId> ckRecordId)
    {
        return _inputRecordTypes[ckRecordId];
    }

    public RtEnumScalarType GetEnum(RtCkId<CkEnumId> ckEnumId)
    {
        return _enumTypes[ckEnumId];
    }

    /// <inheritdoc />
    public DynamicConnectionType GetOrCreateInterfaceAssociationConnection(
        RtCkId<CkTypeId> baseCkTypeId,
        string navigationPropertyName,
        Func<DynamicConnectionType> factory)
    {
        // Use empty string for allowedTypesKey since InterfaceAssociationField doesn't pass allowedTypes
        // This maintains backward compatibility with interface type creation
        return _interfaceAssociationConnections.GetOrAdd((baseCkTypeId, navigationPropertyName, string.Empty), _ => factory());
    }

    /// <inheritdoc />
    public bool TryGetInterfaceAssociationConnection(
        RtCkId<CkTypeId> baseCkTypeId,
        string navigationPropertyName,
        IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        out DynamicConnectionType? connectionType)
    {
        // Create a stable key from the sorted allowedTypes to ensure consistent cache hits
        // This prevents using a cached connection with different allowedTypes
        var allowedTypesKey = CreateAllowedTypesKey(allowedTypes);
        return _interfaceAssociationConnections.TryGetValue((baseCkTypeId, navigationPropertyName, allowedTypesKey), out connectionType);
    }

    /// <summary>
    ///     Creates a stable string key from a list of allowed types for cache lookup.
    ///     The types are sorted to ensure consistent keys regardless of input order.
    /// </summary>
    private static string CreateAllowedTypesKey(IReadOnlyList<RtCkId<CkTypeId>> allowedTypes)
    {
        if (allowedTypes.Count == 0)
            return string.Empty;

        // Sort by the full name to ensure consistent ordering
        var sortedNames = allowedTypes
            .Select(t => t.SemanticVersionedFullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return string.Join("|", sortedNames);
    }

    /// <inheritdoc />
    public IGraphType[] GetKnownGraphTypes()
    {
        var inputTypes = new List<IGraphType>();
        inputTypes.AddRange(_types.Values);
        inputTypes.AddRange(_inputTypes.Values);
        inputTypes.AddRange(_interfaceTypes.Values);
        inputTypes.AddRange(_enumTypes.Values);
        inputTypes.AddRange(_recordTypes.Values);
        inputTypes.AddRange(_inputRecordTypes.Values);

        // Register query row types implementing the RtQueryRow interface
        inputTypes.Add(new RtSimpleQueryRowDtoType());
        inputTypes.Add(new RtAggregationQueryRowDtoType());
        inputTypes.Add(new RtGroupingAggregationQueryRowDtoType());

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
            var rtCkEnumId = ckEnumGraph.CkEnumId.ToRtCkId();
            var rtEnumType = _enumTypes.GetOrAdd(rtCkEnumId, new RtEnumScalarType(rtCkEnumId));
            rtEnumType.Populate(ckEnumGraph);
        }

        // Make records second, because types depend on it.
        foreach (var ckRecordGraph in _ckCacheService.GetCkRecords(_tenantId))
        {
            var rtCkRecordId = ckRecordGraph.CkRecordId.ToRtCkId();
            _recordTypes.TryAdd(rtCkRecordId, new RtRecordDtoType(rtCkRecordId));

            if (!ckRecordGraph.IsAbstract)
            {
                _inputRecordTypes.TryAdd(rtCkRecordId,
                    new RtRecordDtoInputType(rtCkRecordId));
            }
        }

        foreach (var rtRecordDtoType in _recordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetRtCkRecord(_tenantId, rtRecordDtoType.CkRecordId);
            rtRecordDtoType.Populate(_options, this, ckRecordGraph);
        }

        foreach (var rtRecordDtoInputType in _inputRecordTypes.Values)
        {
            var ckRecordGraph = _ckCacheService.GetRtCkRecord(_tenantId, rtRecordDtoInputType.CkRecordId);
            rtRecordDtoInputType.Populate(_options, this, ckRecordGraph);
        }

        foreach (var ckTypeGraph in _ckCacheService.GetCkTypes(_tenantId))
        {
            var rtCkTypeId = ckTypeGraph.CkTypeId.ToRtCkId();

            // Create object types for ALL types (including abstract) to enable query endpoints
            _types.TryAdd(rtCkTypeId, new RtEntityDtoType(ckTypeGraph));

            if (ckTypeGraph.IsAbstract)
            {
                // For abstract types, ALSO create interface types
                // This enables fragment inheritance where a fragment on a base type matches derived types
                // The interface has a different name (suffix "Interface") to avoid GraphQL name collision
                _interfaceTypes.TryAdd(rtCkTypeId, new RtEntityInterfaceType(ckTypeGraph));
            }
            else
            {
                // For concrete types, also create input types (abstract types can't have input types)
                var rtEntityDtoInputType =
                    _inputTypes.GetOrAdd(rtCkTypeId, new RtEntityDtoInputType(rtCkTypeId));
                rtEntityDtoInputType.Populate(_options, _ckCacheService, _tenantId, this, ckTypeGraph);
            }

            if (ckTypeGraph.IsStreamType)
            {
                _tsTypes.TryAdd(rtCkTypeId, new StreamDataEntityDtoType(ckTypeGraph));
            }
        }

        // Populate interface types first (they need to be populated before object types
        // so that object types can implement them)
        foreach (var rtEntityInterfaceType in _interfaceTypes.Values)
        {
            rtEntityInterfaceType.Populate(_options, _ckCacheService, _tenantId, this);
        }

        foreach (var rtEntityDtoType in _types.Values)
        {
            rtEntityDtoType.Populate(_options, _ckCacheService, _tenantId, this);
        }

        foreach (var tsEntityDtoType in _tsTypes.Values)
        {
            tsEntityDtoType.Populate(this);
        }
    }
}