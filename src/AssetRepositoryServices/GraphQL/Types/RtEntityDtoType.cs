using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.DistributionEventHub.Sagas;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Runtime Entity Type
/// </summary>
public sealed class RtEntityDtoType : ObjectGraphType<RtEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <inheritdoc />
    public RtEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        Name = _ckTypeGraph.CkTypeId.GetGraphQlName();
        Description = $"Runtime entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is RtEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, type: typeof(NonNullGraphType<CkIdType<CkTypeId>>));
        Field(d => d.RtCreationDateTime, type: typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, type: typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;

    /// <summary>
    /// Returns true if the type is abstract
    /// </summary>
    public bool IsAbstract => _ckTypeGraph.IsAbstract;


    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache,
        IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor)
    {
        AddConstructionKit();

        foreach (var attribute in _ckTypeGraph.AllAttributes.Values)
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, false);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.Out.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(graphTypesCache, dataLoaderAccessor, sessionAccessor, ckTypeAssociationGraph.NavigationPropertyName,
                allowedTypes.Select(x => x.InheritorCkTypeId).Distinct().ToList(), _ckTypeGraph.CkTypeId,
                ckTypeAssociationGraph.CkRoleId, GraphDirections.Outbound);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.In.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(graphTypesCache, dataLoaderAccessor, sessionAccessor, ckTypeAssociationGraph.NavigationPropertyName,
                allowedTypes.Select(x => x.InheritorCkTypeId).Distinct().ToList(), _ckTypeGraph.CkTypeId,
                ckTypeAssociationGraph.CkRoleId, GraphDirections.Inbound);
        }
    }

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<RtEntityDto> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckTypeGraph = ckCacheService.GetCkType(graphQlUserContext.TenantId, arg.Source.CkTypeId);
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
    }

    internal static RtEntityDto CreateRtEntityDto(RtEntity rtEntity)
    {
        var rtEntityDto = new RtEntityDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId,
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            UserContext = rtEntity
        };
        return rtEntityDto;
    }
}