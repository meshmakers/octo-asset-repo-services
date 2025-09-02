using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Represents a GraphQL type for a runtime entity generic association DTO in OctoMesh.
/// </summary>
public sealed class RtAssociationDtoType : ObjectGraphType<RtAssociationDto>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RtAssociationDtoType" /> class.
    /// </summary>
    public RtAssociationDtoType()
    {
        Name = "RtAssociation";
        Description = "A runtime association type of OctoMesh";

        Field(x => x.CkAssociationRoleId, typeof(NonNullGraphType<CkIdGraph<CkAssociationRoleId>>));
        Field(x => x.TargetRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(x => x.TargetCkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>));
        Field(x => x.OriginRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(x => x.OriginCkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>));

        Connection<RtEntityAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtAssociationDto> context)
    {
        var ckCacheService = context.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)context.UserContext;


        var ckAssociationRole =
            ckCacheService.GetCkAssociationRole(graphQlContext.TenantId, context.Source.CkAssociationRoleId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        if (context.HasArgument(Statics.AttributeNamesFilterArg))
        {
            var filterAttributeNames = context.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

            resultList =
                ckAssociationRole.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }
        else
        {
            resultList = ckAssociationRole.AllAttributes.Values;
        }

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtEntityAttributeDto((RtAssociation)context.Source.UserContext!, item)),
            context);
    }

    private RtEntityAttributeDto CreateRtEntityAttributeDto(RtAssociation rtAssociationDto,
        CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var attributeDto = new RtEntityAttributeDto
        {
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            Value = rtAssociationDto.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName)
        };
        return attributeDto;
    }

    internal static RtAssociationDto CreateRtAssociationDto(RtAssociation rtAssociation)
    {
        var rtAssociationDto = new RtAssociationDto
        {
            OriginRtId = rtAssociation.OriginRtId,
            OriginCkTypeId = rtAssociation.OriginCkTypeId,
            TargetRtId = rtAssociation.TargetRtId,
            TargetCkTypeId = rtAssociation.TargetCkTypeId,
            CkAssociationRoleId = rtAssociation.AssociationRoleId ??
                                  throw OctoGraphQLException.CkAssociationRoleIdUndefined(),
            UserContext = rtAssociation
        };
        return rtAssociationDto;
    }
}