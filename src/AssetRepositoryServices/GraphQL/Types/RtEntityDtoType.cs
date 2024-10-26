using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

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

        Field(d => d.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, type: typeof(CkIdTypeGraph<CkTypeId>));
        Field(d => d.RtCreationDateTime, type: typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, type: typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);
    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;

    /// <summary>
    /// Returns true if the type is abstract
    /// </summary>
    public bool IsAbstract => _ckTypeGraph.IsAbstract;

    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();
        AddGenericAssociations();

        foreach (var attribute in _ckTypeGraph.AllAttributes.Values)
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, false);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.Out.All.GroupBy(x => x.NavigationPropertyName))
        {
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.TargetCkTypeId).GetAllDerivedTypes(true))
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
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
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(graphTypesCache, ckTypeAssociationGraph.Key,
                allowedTypes.Select(x => x).Distinct().ToList(), _ckTypeGraph.CkTypeId,
                ckTypeAssociationGraph.First().CkRoleId, GraphDirections.Inbound);
        }
    }

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkEntity);
    }

    private void AddGenericAssociations()
    {
        Connection<RtEntityGenericDtoType>("Associations")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.RoleIdArg, "The role id of the association.")
            .Argument<BooleanGraphType>(Statics.IncludeIndirectArg,
                "Include indirect associations, otherwise direct associations are returned.")
            .Argument<NonNullGraphType<GraphDirectionsDtoType>>(Statics.DirectionArg,
                "The direction of the association.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg, "The construction kit type with the given id.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<FieldGroupByType>(Statics.GroupByArg, "Groups items based on attributes")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveGenericRtAssociationsQuery);
    }

    private async Task<object?> ResolveGenericRtAssociationsQuery(IResolveConnectionContext<RtEntityDto> arg)
    {
        await Task.Yield();

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        if (!arg.TryGetArgument(Statics.RoleIdArg, out string? roleId))
        {
            throw AssetRepositoryException.RoleIdMissing();
        }

        if (!arg.TryGetArgument(Statics.IncludeIndirectArg, out bool? indirectAssociations))
        {
            indirectAssociations = false;
        }

        if (!arg.TryGetArgument(Statics.DirectionArg, out GraphDirections? direction))
        {
            throw AssetRepositoryException.DirectionMissing();
        }

        if (!arg.TryGetArgument(Statics.CkId, out string? ckIdObj))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }
        
        CkId<CkTypeId> targetCkId = new(ckIdObj);

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        if (indirectAssociations.Value)
        {
            var result = await tenantRepository.GetIndirectRtAssociationTargetsAsync(
                sessionAccessor.Session, new[] { arg.Source.RtId }, CkTypeId, new CkId<CkAssociationRoleId>(roleId),
                direction.Value,
                null, targetCkId, dataQueryOperation, offset, arg.First);

            return ConnectionUtils.ToConnection(result.First().Value.Items.Select(CreateRtEntityDto), arg, null);
        }
        else
        {
            var result = await tenantRepository.GetRtAssociationTargetsAsync(
                sessionAccessor.Session, new[] { arg.Source.RtId }, CkTypeId, new CkId<CkAssociationRoleId>(roleId),
                targetCkId, direction.Value,
                null, dataQueryOperation, offset, arg.First);

            return ConnectionUtils.ToConnection(result.First().Value.Items.Select(CreateRtEntityDto), arg, null);
        }
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