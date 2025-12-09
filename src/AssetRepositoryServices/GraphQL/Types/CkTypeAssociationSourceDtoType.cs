using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeAssociationSourceDtoType : ObjectGraphType<CkTypeAssociationSourceDto>
{
    public CkTypeAssociationSourceDtoType()
    {
        Name = "CkTypeAssociationSource";
        Description = "Associations of a construction kit type";

        Field(x => x.All, typeof(ListGraphType<CkTypeAssociationDtoType>))
            .Description("All associations definitions available current type")
            .Returns<IEnumerable<CkTypeAssociationDto>>()
            .Resolve(ResolveAllAssociations);
        Field(x => x.Inherited, typeof(ListGraphType<CkTypeAssociationDtoType>))
            .Description("Associations definitions inherited by base types")
            .Returns<IEnumerable<CkTypeAssociationDto>>()
            .Resolve(ResolveInheritedAssociations);
        Field(x => x.Owned, typeof(ListGraphType<CkTypeAssociationDtoType>))
            .Description("Associations definitions defined by the current type")
            .Returns<IEnumerable<CkTypeAssociationDto>>()
            .Resolve(ResolveOwnedAssociations);
    }

    private IEnumerable<CkTypeAssociationDto> ResolveOwnedAssociations(
        IResolveFieldContext<CkTypeAssociationSourceDto> ctx)
    {
        var ckCacheService = ctx.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)ctx.UserContext;
        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId);

        switch (ctx.Source.Direction)
        {
            case GraphDirections.Outbound:
                return ckTypeGraph.Associations.Out.Owned.Select(CreateCkTypeAssociationDto);
            case GraphDirections.Inbound:
                return ckTypeGraph.Associations.In.Owned.Select(CreateCkTypeAssociationDto);
            default:
                throw OctoGraphQLException.DirectionNotSupported(ctx.Source.Direction);
        }
    }


    private IEnumerable<CkTypeAssociationDto> ResolveInheritedAssociations(
        IResolveFieldContext<CkTypeAssociationSourceDto> ctx)
    {
        var ckCacheService = ctx.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)ctx.UserContext;
        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId);

        switch (ctx.Source.Direction)
        {
            case GraphDirections.Outbound:
                return ckTypeGraph.Associations.Out.Inherited.Select(CreateCkTypeAssociationDto);
            case GraphDirections.Inbound:
                return ckTypeGraph.Associations.In.Inherited.Select(CreateCkTypeAssociationDto);
            default:
                throw OctoGraphQLException.DirectionNotSupported(ctx.Source.Direction);
        }
    }

    private IEnumerable<CkTypeAssociationDto> ResolveAllAssociations(
        IResolveFieldContext<CkTypeAssociationSourceDto> ctx)
    {
        var ckCacheService = ctx.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)ctx.UserContext;
        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId);

        switch (ctx.Source.Direction)
        {
            case GraphDirections.Outbound:
                return ckTypeGraph.Associations.Out.All.Select(CreateCkTypeAssociationDto);
            case GraphDirections.Inbound:
                return ckTypeGraph.Associations.In.All.Select(CreateCkTypeAssociationDto);
            default:
                throw OctoGraphQLException.DirectionNotSupported(ctx.Source.Direction);
        }
    }

    private CkTypeAssociationDto CreateCkTypeAssociationDto(CkTypeAssociationGraph typeAssociationGraph)
    {
        return new CkTypeAssociationDto
        {
            RoleId = typeAssociationGraph.CkRoleId,
            OriginCkTypeId = typeAssociationGraph.OriginCkTypeId,
            Multiplicity =  typeAssociationGraph.Multiplicity,
            NavigationPropertyName =  typeAssociationGraph.NavigationPropertyName,
            TargetCkTypeId = typeAssociationGraph.TargetCkTypeId
        };
    }

}