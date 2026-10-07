using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

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
        var graphQlContext = (GraphQlUserContext)context.UserContext;
        var attributeDtos = CreateAttributeDtos(context.GetCkCacheService(), context.GetProtector(),
            context.RequestServices?.GetService<ILogger<RtAssociationDtoType>>(), graphQlContext.TenantId,
            (RtAssociation)context.Source.UserContext!, context.Source.CkAssociationRoleId,
            RtEntityGenericDtoType.GetAttributeNamesFilter(context));
        return ConnectionUtils.ToOctoConnection(attributeDtos, context);
    }

    /// <summary>
    ///     The generic attribute projection of an association (AB#5535): an empty filter returns nothing without
    ///     touching the CK cache; an association whose role is not in the (loaded) CK cache is projected from its
    ///     stored attributes by <see cref="UnknownCkTypeAttributeProjection" /> instead of failing the whole list.
    /// </summary>
    internal static List<RtEntityAttributeDto> CreateAttributeDtos(ICkCacheService ckCacheService,
        ISecretAttributeProtector? protector, ILogger? logger, string tenantId, RtAssociation rtAssociation,
        RtCkId<CkAssociationRoleId> ckAssociationRoleId, IReadOnlyCollection<string>? filterAttributeNames)
    {
        if (filterAttributeNames is { Count: 0 })
        {
            return [];
        }

        CkAssociationRoleGraph ckAssociationRole;
        try
        {
            ckAssociationRole = ckCacheService.GetRtCkAssociationRole(tenantId, ckAssociationRoleId);
        }
        catch (CkCacheException) when (ckCacheService.IsTenantLoaded(tenantId))
        {
            // Loaded cache without this role: outdated/removed model element (no TryGet exists for roles).
            UnknownCkTypeAttributeProjection.WarnOnce(logger, tenantId, "CK association role",
                ckAssociationRoleId.ToString());
            return UnknownCkTypeAttributeProjection.Project(rtAssociation, filterAttributeNames,
                rtRecord => RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, protector, tenantId,
                    rtRecord, false, null, logger),
                protector, name => AccessQueryGuard.IsHiddenName(ckCacheService, tenantId, name));
        }

        // CK v2 (AB#5668): hidden association attributes are not projected.
        var visibleAttributes = ckAssociationRole.AllAttributes.Values.Where(a => !AccessQueryGuard.IsHidden(a));
        var resultList = filterAttributeNames != null
            ? visibleAttributes.Where(a => filterAttributeNames.Contains(a.AttributeName.ToCamelCase()))
            : visibleAttributes;

        return resultList.Select(item => CreateRtEntityAttributeDto(rtAssociation, item, protector)).ToList();
    }

    private static RtEntityAttributeDto CreateRtEntityAttributeDto(RtAssociation rtAssociationDto,
        CkTypeAttributeGraph ckTypeAttributeGraph, ISecretAttributeProtector? protector)
    {
        var value = rtAssociationDto.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName);

        // AB#5528: association roles cannot declare Secret attributes (compiler rule); a secret found anyway
        // is projected like on entities - never its value.
        if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Secret || value is RtSecretValue)
        {
            return SecretAttributeProjection.ToAttributeDto(ckTypeAttributeGraph.AttributeName.ToCamelCase(), value,
                protector);
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