using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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

        Field(x => x.CkAssociationRoleId, typeof(NonNullGraphType<RtCkIdGraph<CkAssociationRoleId>>));
        Field(x => x.TargetRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(x => x.TargetCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(x => x.OriginRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(x => x.OriginCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));

        Connection<RtEntityAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtAssociationDto> context)
    {
        var ckCacheService = context.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)context.UserContext;


        var ckAssociationRole =
            ckCacheService.GetRtCkAssociationRole(graphQlContext.TenantId, context.Source.CkAssociationRoleId);

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

        return ConnectionUtils.ToOctoConnection(
            resultList.Select(item => CreateRtEntityAttributeDto((RtAssociation)context.Source.UserContext!, item)),
            context);
    }

    private RtEntityAttributeDto CreateRtEntityAttributeDto(RtAssociation rtAssociationDto,
        CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var value = rtAssociationDto.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName);

        // AB#5528: association roles cannot declare Secret attributes (compiler rule); a secret found anyway
        // is projected like on entities - never its value.
        if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Secret || value is RtSecretValue)
        {
            return new RtEntityAttributeDto
            {
                AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
                Value = null,
                SecretIsSet = SecretAttributeProjection.IsSet(value)
            };
        }

        var attributeDto = new RtEntityAttributeDto
        {
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            Value = value
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