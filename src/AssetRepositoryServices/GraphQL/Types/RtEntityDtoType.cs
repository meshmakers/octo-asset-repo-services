using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Runtime Entity Type
/// </summary>
[DoNotRegister]
internal sealed class RtEntityDtoType : ObjectGraphType<RtEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <inheritdoc />
    public RtEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        Name = _ckTypeGraph.CkTypeId.GetGraphQlPascalCaseName();
        Description = $"Runtime entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is RtEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, typeof(CkIdGraph<CkTypeId>));
        Field(d => d.RtCreationDateTime, typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);
    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;

    /// <summary>
    ///     Returns true if the type is abstract
    /// </summary>
    public bool IsAbstract => _ckTypeGraph.IsAbstract;

    internal void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, ICkCacheService ckCacheService,
        string tenantId, IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();
        AddGenericAssociations();

        var builder = OctoBuilder<RtEntityDto>.Create(this, options);
        foreach (var attribute in _ckTypeGraph.AllAttributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, false);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.Out.All.GroupBy(x => x.NavigationPropertyName))
        {
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.TargetCkTypeId).GetAllDerivedTypes(true))
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All Ck types are abstract for that association
            }

            this.AssociationField(graphTypesCache, ckTypeAssociationGraph.Key,
                allowedTypes.Select(x => x).Distinct().ToList(), _ckTypeGraph.CkTypeId,
                ckTypeAssociationGraph.First().CkRoleId, GraphDirections.Outbound);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.In.All.GroupBy(x => x.NavigationPropertyName))
        {
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.OriginCkTypeId).GetAllDerivedTypes(true))
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All Ck types are abstract for that association
            }

            this.AssociationField(graphTypesCache, ckTypeAssociationGraph.Key,
                allowedTypes.Select(x => x).Distinct().ToList(), _ckTypeGraph.CkTypeId,
                ckTypeAssociationGraph.First().CkRoleId, GraphDirections.Inbound);
        }
    }

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkType);
    }

    private void AddGenericAssociations()
    {
        Connection<RtEntityGenericDtoType>("Associations")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.RoleIdArg, "The role id of the association.")
            .Argument<BooleanGraphType>(Statics.IncludeIndirectArg,
                "Include indirect associations, otherwise direct associations are returned.")
            .Argument<NonNullGraphType<GraphDirectionsDtoType>>(Statics.DirectionArg,
                "The direction of the association.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveGenericRtAssociationsQuery);
    }

    private async Task<object?> ResolveGenericRtAssociationsQuery(IResolveConnectionContext<RtEntityDto> arg)
    {
        var sessionAccessor = arg.GetSessionAccessor();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        if (!arg.TryGetArgument(Statics.IncludeIndirectArg, out bool? indirectAssociations))
        {
            indirectAssociations = false;
        }


        var roleId = arg.GetArgument<string>(Statics.RoleIdArg);
        var direction = arg.GetArgument<GraphDirections>(Statics.DirectionArg);
        var targetCkId = arg.GetArgument<CkId<CkTypeId>>(Statics.CkId);

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        if (indirectAssociations.Value)
        {
            var result = await tenantRepository.GetIndirectRtAssociationTargetsAsync(
                sessionAccessor.Session, [arg.Source.RtId], CkTypeId, new CkId<CkAssociationRoleId>(roleId),
                direction,
                null, targetCkId, dataQueryOperation, offset, arg.First);

            return ConnectionUtils.ToConnection(result.First().Value.Items.Select(CreateRtEntityDto), arg);
        }
        else
        {
            var result = await tenantRepository.GetRtAssociationTargetsAsync(
                sessionAccessor.Session, [arg.Source.RtId], CkTypeId, new CkId<CkAssociationRoleId>(roleId),
                targetCkId, direction,
                null, dataQueryOperation, offset, arg.First);

            return ConnectionUtils.ToConnection(result.First().Value.Items.Select(CreateRtEntityDto), arg);
        }
    }

    private object ResolveCkType(IResolveFieldContext<RtEntityDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckTypeGraph = ckCacheService.GetCkType(graphQlUserContext.TenantId, arg.Source.CkTypeId);
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
    }

    internal static RtEntityDto CreateRtEntityDto(RtEntity rtEntity)
    {
        var rtEntityDto = new RtEntityDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            RtVersion = rtEntity.RtVersion,
            UserContext = rtEntity
        };
        return rtEntityDto;
    }
}