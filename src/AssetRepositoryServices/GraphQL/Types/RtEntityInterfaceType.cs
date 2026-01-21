using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     A GraphQL Interface type for abstract Construction Kit types.
///     This enables fragment inheritance where a fragment on a base type
///     matches entities of derived types.
/// </summary>
[DoNotRegister]
internal sealed class RtEntityInterfaceType : InterfaceGraphType<RtEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <summary>
    ///     Creates a new interface type for an abstract CK type.
    /// </summary>
    /// <param name="ckTypeGraph">The CK type graph for the abstract type</param>
    public RtEntityInterfaceType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        // Use a different name than the object type to avoid GraphQL name collision
        // The interface is used for fragment inheritance on abstract types
        Name = _ckTypeGraph.CkTypeId.ToRtCkId().GetGraphQlPascalCaseName() + "Interface";
        Description = $"Interface for runtime entities of construction kit type '{_ckTypeGraph.CkTypeId}'";

        // Add common fields that all derived types will have
        Field(d => d.RtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.RtCreationDateTime, typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);

        // ResolveType determines the concrete type for a given object
        // The actual resolution happens in the schema's type resolution
        // This is a fallback that returns null to let GraphQL.NET use IsTypeOf
        ResolveType = _ => null;
    }

    /// <summary>
    ///     Returns the Construction Kit ID of the interface type
    /// </summary>
    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;

    /// <summary>
    ///     Populates the interface with attribute and association fields from the abstract type.
    /// </summary>
    internal void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, ICkCacheService ckCacheService,
        string tenantId, IGraphTypesCache graphTypesCache)
    {
        var builder = OctoBuilder<RtEntityDto>.Create(this, options);
        foreach (var attribute in _ckTypeGraph.AllAttributes.Values)
        {
            // Pass isInterface=true to prevent setting resolvers on interface fields
            builder.Attribute(graphTypesCache, attribute, isInputType: false, isInterface: true);
        }

        // Add outbound association fields
        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.Out.All.GroupBy(x => x.NavigationPropertyName))
        {
            // Get all derived types but filter out abstract types since they can't have instances
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.TargetCkTypeId).GetAllDerivedTypes(true))
                .Where(x => !ckCacheService.GetCkType(tenantId, x).IsAbstract)
                .Select(x => x.ToRtCkId())
                .Distinct()
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All CK types are abstract for that association
            }

            // Use the OriginCkTypeId as the cache key - this is where the association is defined
            // This ensures all interfaces that inherit this association use the same connection type
            var originCkTypeId = ckTypeAssociationGraph.First().OriginCkTypeId.ToRtCkId();
            // The queryBaseType is the target of the association (used for union naming)
            var queryBaseType = ckTypeAssociationGraph.First().TargetCkTypeId.ToRtCkId();
            this.InterfaceAssociationField(graphTypesCache, ckTypeAssociationGraph.Key, allowedTypes, originCkTypeId, queryBaseType);
        }

        // Add inbound association fields
        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.In.All.GroupBy(x => x.NavigationPropertyName))
        {
            // Get all derived types but filter out abstract types since they can't have instances
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.OriginCkTypeId).GetAllDerivedTypes(true))
                .Where(x => !ckCacheService.GetCkType(tenantId, x).IsAbstract)
                .Select(x => x.ToRtCkId())
                .Distinct()
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All CK types are abstract for that association
            }

            // For inbound associations, use the OriginCkTypeId as the cache key (queryBaseType)
            // This must match what RtEntityDtoType uses in AssociationField for cache lookup
            // The queryBaseType is the origin of the inbound association (the types that point to this type)
            var queryBaseType = ckTypeAssociationGraph.First().OriginCkTypeId.ToRtCkId();
            this.InterfaceAssociationField(graphTypesCache, ckTypeAssociationGraph.Key, allowedTypes, queryBaseType, queryBaseType);
        }
    }
}
