using System.Collections.Concurrent;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ConcurrentDictionary<RtCkId<CkInterfaceId>, CkInterfaceGraphType> _ckInterfaceTypes;
    private readonly ILogger _logger;
    private readonly IOctoService _octoService;
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;

    private readonly ConcurrentDictionary<RtCkId<CkRecordId>, RtRecordDtoType> _recordTypes;
    private readonly string _tenantId;
    private readonly ConcurrentDictionary<RtCkId<CkTypeId>, RtEntityDtoType> _types;
    private readonly ConcurrentDictionary<(RtCkId<CkTypeId>, string, string), DynamicConnectionType> _interfaceAssociationConnections;


    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService"></param>
    /// <param name="options"></param>
    /// <param name="tenantId"></param>
    /// <param name="ckCacheService"></param>
    /// <param name="logger">Logger for schema-build diagnostics; <c>null</c> = none</param>
    public GraphTypesCache(ICkCacheService ckCacheService, IOctoService octoService,
        IOptions<OctoAssetRepositoryServicesOptions> options, string tenantId, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _ckInterfaceTypes = new ConcurrentDictionary<RtCkId<CkInterfaceId>, CkInterfaceGraphType>();
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


    public RtEntityDtoType GetType(RtCkId<CkTypeId> ckTypeId)
    {
        return _types[ckTypeId];
    }

    /// <inheritdoc />
    public IReadOnlyList<IInterfaceGraphType> GetImplementedInterfaces(RtCkId<CkTypeId> ckTypeId)
    {
        var interfaces = new List<IInterfaceGraphType>();
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

        // CK v2 (AB#5667): CK interfaces implemented directly or through a base type.
        foreach (var ckInterfaceId in ckTypeGraph.AllImplementedInterfaces)
        {
            if (_ckInterfaceTypes.TryGetValue(ckInterfaceId.ToRtCkId(), out var ckInterfaceType))
            {
                interfaces.Add(ckInterfaceType);
            }
        }

        return interfaces;
    }

    private void LinkExtendedInterfaces(CkInterfaceGraph ckInterfaceGraph)
    {
        var child = _ckInterfaceTypes[ckInterfaceGraph.CkInterfaceId.ToRtCkId()];
        foreach (var parentId in ckInterfaceGraph.AllExtendedInterfaces)
        {
            if (!_ckInterfaceTypes.TryGetValue(parentId.ToRtCkId(), out var parent))
            {
                continue;
            }

            // The compiler merges the parent's members into the child (AllAttributes), so the fields align; this is
            // the safety net GraphQL schema validation would otherwise turn into a failed schema build.
            var missing = parent.Fields.FirstOrDefault(pf => child.Fields.All(cf => cf.Name != pf.Name));
            if (missing != null)
            {
                ReportInterfaceNotImplemented(child.Name, parent, $"field '{missing.Name}' is missing");
                continue;
            }

            child.AddResolvedInterface(parent);
        }
    }

    private void AddInterfaceAssociationFields(CkInterfaceGraph ckInterfaceGraph)
    {
        var interfaceType = _ckInterfaceTypes[ckInterfaceGraph.CkInterfaceId.ToRtCkId()];
        var implementors = _types.Values.Where(t => t.ResolvedInterfaces.Contains(interfaceType)).ToList();

        foreach (var association in ckInterfaceGraph.AllAssociations)
        {
            var roleId = association.Definition.CkRoleId;

            // Inherited from a parent interface that could add the field: reuse it (the child's implementors are a
            // subset of the parent's, so they have the same field).
            var inherited = interfaceType.ResolvedInterfaces
                .SelectMany(p => p.Fields)
                .FirstOrDefault(f => f.Metadata.TryGetValue(Statics.RoleId, out var r) &&
                                     Equals(r, roleId.ToRtCkId()));
            if (inherited != null)
            {
                if (interfaceType.Fields.All(f => f.Name != inherited.Name))
                {
                    interfaceType.AddField(CloneInterfaceField(inherited, roleId));
                }

                continue;
            }

            if (implementors.Count == 0)
            {
                _logger.LogDebug(
                    "CK interface {InterfaceName} of tenant {TenantId}: association {RoleId} has no implementing type; no field",
                    interfaceType.Name, _tenantId, roleId);
                continue;
            }

            var fields = implementors.Select(t => (Type: t, Field: FindAssociationField(t, roleId))).ToList();
            var first = fields[0].Field;
            var mismatch = fields.FirstOrDefault(f => f.Field == null || first == null ||
                                                      f.Field.Name != first.Name ||
                                                      GetTypeName(f.Field) != GetTypeName(first) ||
                                                      !SameArguments(f.Field, first));
            if (first == null || mismatch.Type != null)
            {
                _logger.LogWarning(
                    "CK interface {InterfaceName} of tenant {TenantId}: association {RoleId} is not exposed as an interface field because the implementing types do not expose the same field shape (first mismatch: {TypeName})",
                    interfaceType.Name, _tenantId, roleId, (mismatch.Type ?? fields[0].Type).Name);
                continue;
            }

            interfaceType.AddField(CloneInterfaceField(first, roleId));
        }
    }

    private static FieldType? FindAssociationField(RtEntityDtoType type, CkId<CkAssociationRoleId> roleId)
    {
        var rtRoleId = roleId.ToRtCkId();
        return type.Fields.FirstOrDefault(f =>
            f.Metadata.TryGetValue(Statics.RoleId, out var r) && Equals(r, rtRoleId) &&
            f.Metadata.TryGetValue(Statics.GraphDirection, out var d) && Equals(d, GraphDirections.Outbound));
    }

    private static FieldType CloneInterfaceField(FieldType source, CkId<CkAssociationRoleId> roleId)
    {
        var field = new FieldType
        {
            Name = source.Name,
            Description = source.Description,
            Type = source.Type,
            ResolvedType = source.ResolvedType,
            Arguments = source.Arguments
        };
        field.Metadata[Statics.RoleId] = roleId.ToRtCkId();
        return field;
    }

    private static string? GetTypeName(FieldType field)
    {
        return field.ResolvedType?.Name ?? field.Type?.Name;
    }

    private static bool SameArguments(FieldType a, FieldType b)
    {
        var argsA = (a.Arguments?.Select(x => x.Name) ?? []).OrderBy(x => x, StringComparer.Ordinal);
        var argsB = (b.Arguments?.Select(x => x.Name) ?? []).OrderBy(x => x, StringComparer.Ordinal);
        return argsA.SequenceEqual(argsB);
    }

    /// <inheritdoc />
    public void ReportInterfaceNotImplemented(string objectTypeName, IInterfaceGraphType interfaceType, string reason)
    {
        if (interfaceType is CkInterfaceGraphType)
        {
            _logger.LogWarning(
                "GraphQL type {TypeName} of tenant {TenantId} does not implement CK interface {InterfaceName}: {Reason}",
                objectTypeName, _tenantId, interfaceType.Name, reason);
        }
        else
        {
            _logger.LogDebug(
                "GraphQL type {TypeName} of tenant {TenantId} does not implement {InterfaceName}: {Reason}",
                objectTypeName, _tenantId, interfaceType.Name, reason);
        }
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
        IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        Func<DynamicConnectionType> factory)
    {
        // Use the same allowedTypesKey format that TryGetInterfaceAssociationConnection uses
        // This ensures implementing types can find the cached connection type
        var allowedTypesKey = CreateAllowedTypesKey(allowedTypes);
        return _interfaceAssociationConnections.GetOrAdd((baseCkTypeId, navigationPropertyName, allowedTypesKey), _ => factory());
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
        // CK v2 (AB#5667): registered even though no query field returns them yet (introspection, fragments).
        inputTypes.AddRange(_ckInterfaceTypes.Values);
        inputTypes.AddRange(_enumTypes.Values);
        inputTypes.AddRange(_recordTypes.Values);
        inputTypes.AddRange(_inputRecordTypes.Values);

        // Register query row types implementing the RtQueryRow interface
        inputTypes.Add(new RtSimpleQueryRowDtoType());
        inputTypes.Add(new RtAggregationQueryRowDtoType());
        inputTypes.Add(new RtGroupingAggregationQueryRowDtoType());

        return inputTypes.ToArray();
    }

    /// <summary>
    ///     Returns counters for the schema-build timing log (CK v2 risk R8).
    /// </summary>
    public GraphTypesCacheStatistics GetStatistics()
    {
        return new GraphTypesCacheStatistics(_types.Count, CkInterfaceCount: _ckInterfaceTypes.Count);
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

        // CK v2 (AB#5667): CK interfaces after enums and records (members may use them), before the types
        // (object types add them as implemented interfaces while they are populated).
        var ckInterfaceGraphs = _ckCacheService.GetRtCkInterfaces(_tenantId).ToList();
        foreach (var ckInterfaceGraph in ckInterfaceGraphs)
        {
            var ckInterfaceType = _ckInterfaceTypes.GetOrAdd(ckInterfaceGraph.CkInterfaceId.ToRtCkId(),
                _ => new CkInterfaceGraphType(ckInterfaceGraph));
            ckInterfaceType.Populate(_options, this, ckInterfaceGraph);
        }

        // CK v2 (F1.5-S2, AB#5921): interface `extends` -> GraphQL interfaces implement their (transitive) parents.
        foreach (var ckInterfaceGraph in ckInterfaceGraphs)
        {
            LinkExtendedInterfaces(ckInterfaceGraph);
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

        // CK v2 (F1.5-S2, AB#5921): interface association members become interface fields when every implementing
        // object type exposes the same field shape. Parents first, so a child interface can inherit the field.
        foreach (var ckInterfaceGraph in ckInterfaceGraphs.OrderBy(g => g.AllExtendedInterfaces.Count))
        {
            AddInterfaceAssociationFields(ckInterfaceGraph);
        }
    }
}

/// <summary>
///     Counters of a populated <see cref="GraphTypesCache" />, used by the schema-build timing log.
/// </summary>
/// <param name="CkTypeCount">Number of CK entity object types</param>
/// <param name="CkInterfaceCount">Number of CK v2 interfaces mapped to GraphQL interface types</param>
internal sealed record GraphTypesCacheStatistics(int CkTypeCount, int CkInterfaceCount);
