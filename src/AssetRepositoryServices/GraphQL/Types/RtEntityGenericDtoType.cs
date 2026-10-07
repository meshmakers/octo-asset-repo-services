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
///     Implements a generic runtime entities type that can be used for generic access to entities
/// </summary>
internal sealed class RtEntityGenericDtoType : ObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityGenericDtoType()
    {
        Name = "RtEntity";
        Description = "A runtime entity type of OctoMesh";
        Field(d => d.RtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtCreatedBy, true)
            .Description("Subject id of the identity that created the entity (engine-stamped; read-only).");
        Field<NonNullGraphType<StringGraphType>>("rtDisplayName")
            .Description("Engine-computed display name (from the CK type's displayNameRule). " +
                         "Falls back to '<ckTypeId>@<rtId>' when no computed value is stored. " +
                         "Filtering and sorting operate on the stored value.")
            .Resolve(ctx => ctx.Source.RtDisplayName ?? $"{ctx.Source.CkTypeId}@{ctx.Source.RtId}");
        Field(x => x.RtDisplayDescription, true)
            .Description("Engine-computed display description (from the CK type's displayDescriptionRule).");
        Field(x => x.RtVersion, true);
        Field("associations", typeof(RtEntityGenericAssociationType)).Description(
                "A list of associations of this entity. The association role id is used to filter the associations.")
            .Resolve(ctx => new RtEntityGenericAssociation(ctx.Source));

        Connection<RtEntityAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames, "When true enum values are resolved to names")
            .Resolve(ResolveAttributes);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtEntityDto> context)
    {
        var graphQlContext = (GraphQlUserContext)context.UserContext;
        context.TryGetArgument(Statics.ResolveEnumValuesToNames, out bool resolveEnumValuesToNames);

        var attributeDtos = CreateAttributeDtos(context.GetCkCacheService(), context.GetProtector(),
            context.RequestServices?.GetService<ILogger<RtEntityGenericDtoType>>(), graphQlContext.TenantId,
            (RtEntity)context.Source.UserContext!, context.Source.CkTypeId, GetAttributeNamesFilter(context),
            resolveEnumValuesToNames);

        return ConnectionUtils.ToOctoConnection(attributeDtos, context);
    }

    /// <summary>
    ///     The <c>attributeNames</c> argument: <c>null</c> when omitted or explicitly null (= all attributes), otherwise
    ///     the requested camelCase names (an empty list requests no attribute).
    /// </summary>
    internal static IReadOnlyCollection<string>? GetAttributeNamesFilter(IResolveFieldContext context)
    {
        if (!context.HasArgument(Statics.AttributeNamesFilterArg))
        {
            return null;
        }

        return context.GetArgument<IEnumerable<string>?>(Statics.AttributeNamesFilterArg)?.ToArray();
    }

    /// <summary>
    ///     The generic attribute projection of an entity (AB#5535): an empty filter returns nothing without touching
    ///     the CK cache; an entity whose CK type is not in the (loaded) CK cache is projected from its stored
    ///     attributes by <see cref="UnknownCkTypeAttributeProjection" /> instead of failing the whole list.
    /// </summary>
    /// <param name="ckCacheService">The CK cache</param>
    /// <param name="protector">The key ring of this process; <c>null</c> = unknown</param>
    /// <param name="logger">Logger for the unknown-type warning; <c>null</c> = no warning</param>
    /// <param name="tenantId">The tenant</param>
    /// <param name="rtEntity">The entity</param>
    /// <param name="ckTypeId">The CK type id of the entity</param>
    /// <param name="filterAttributeNames">camelCase attribute names to return; <c>null</c> = all</param>
    /// <param name="resolveEnumValuesToNames">When true enum values are resolved to names</param>
    internal static List<RtEntityAttributeDto> CreateAttributeDtos(ICkCacheService ckCacheService,
        ISecretAttributeProtector? protector, ILogger? logger, string tenantId, RtTypeWithAttributes rtEntity,
        RtCkId<CkTypeId> ckTypeId, IReadOnlyCollection<string>? filterAttributeNames, bool resolveEnumValuesToNames)
    {
        // No attribute requested: nothing to project, and no reason to look up the CK type.
        if (filterAttributeNames is { Count: 0 })
        {
            return [];
        }

        CkTypeGraph ckTypeGraph;
        if (!ckCacheService.IsTenantLoaded(tenantId))
        {
            // Not loaded at all: fail loudly (degrading would treat every type as unknown).
            ckTypeGraph = ckCacheService.GetRtCkType(tenantId, ckTypeId);
        }
        else if (!ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var foundCkTypeGraph))
        {
            UnknownCkTypeAttributeProjection.WarnOnce(logger, tenantId, "CK type", ckTypeId.ToString());
            return UnknownCkTypeAttributeProjection.Project(rtEntity, filterAttributeNames,
                rtRecord => RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, protector, tenantId,
                    rtRecord, false, filterAttributeNames?.ToArray(), logger),
                protector);
        }
        else
        {
            ckTypeGraph = foundCkTypeGraph;
        }

        // CK v2 (AB#5668): hidden attributes never leave the API, not even through the generic projection.
        var visibleAttributes = ckTypeGraph.AllAttributes.Values.Where(a => !AccessQueryGuard.IsHidden(a));
        var resultList = filterAttributeNames != null
            ? visibleAttributes.Where(a => filterAttributeNames.Contains(a.AttributeName.ToCamelCase()))
            : visibleAttributes;

        return resultList.Select(item => CreateRtEntityAttributeDto(ckCacheService, protector, tenantId, rtEntity,
            item, resolveEnumValuesToNames, filterAttributeNames, logger)).ToList();
    }

    internal static RtEntityAttributeDto CreateRtEntityAttributeDto(ICkCacheService ckCacheService,
        ISecretAttributeProtector? protector, string tenantId, RtTypeWithAttributes rtEntity,
        CkTypeAttributeGraph ckTypeAttributeGraph, bool resolveEnumValuesToNames,
        IEnumerable<string>? filterAttributeNames = null, ILogger? logger = null)
    {
        var value = rtEntity.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName);

        // AB#5528 (concept §4.2, handover §2): a Secret attribute is projected as value = null plus secretIsSet,
        // secretKeyMissing and secretSetAt. Decided by the CK attribute type (change-stream documents carry legacy
        // plain strings that are not normalised); an RtSecretValue under a stale CK cache is covered as well.
        if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Secret || value is RtSecretValue)
        {
            return SecretAttributeProjection.ToAttributeDto(ckTypeAttributeGraph.AttributeName.ToCamelCase(), value,
                protector);
        }

        if (value is RtRecord rtRecord)
        {
            value = RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, protector, tenantId, rtRecord,
                resolveEnumValuesToNames,
                filterAttributeNames?.ToArray(), logger);
        }
        else if (value is IEnumerable<object> rtRecordCandidates)
        {
            value = rtRecordCandidates.Select(listValue =>
            {
                if (listValue is RtRecord rtRecord2)
                {
                    return RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, protector, tenantId,
                        rtRecord2,
                        resolveEnumValuesToNames, filterAttributeNames?.ToArray(), logger);
                }

                return listValue is RtSecretValue ? null : listValue;
            });
        }

        if (resolveEnumValuesToNames)
        {
            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Enum &&
                ckTypeAttributeGraph.ValueCkEnumId != null && value != null)
            {
                // AB#5535: an enum missing from the CK cache leaves the stored keys instead of failing the list.
                if (!ckCacheService.TryGetCkEnum(tenantId, ckTypeAttributeGraph.ValueCkEnumId, out var ckEnumGraph))
                {
                    UnknownCkTypeAttributeProjection.WarnOnce(logger, tenantId, "CK enum",
                        ckTypeAttributeGraph.ValueCkEnumId.ToString());
                }
                else if (value is IEnumerable<object> enumValues)
                {
                    var enumValueList = new List<object>();
                    foreach (var enumValue in enumValues)
                    {
                        if (enumValue is int intEnumValue)
                        {
                            var ckEnumValue = ckEnumGraph.Values.FirstOrDefault(ev => ev.Key == intEnumValue);
                            if (ckEnumValue != null)
                            {
                                enumValueList.Add(ckEnumValue.Name);
                            }
                        }
                        else
                        {
                            enumValueList.Add(enumValue);
                        }
                    }

                    value = enumValueList;
                }
                else if (value is int intEnumValue)
                {
                    var ckEnumValue = ckEnumGraph.Values.FirstOrDefault(ev => ev.Key == intEnumValue);
                    if (ckEnumValue != null)
                    {
                        value = ckEnumValue.Name;
                    }
                }
            }
        }

        var attributeDto = new RtEntityAttributeDto
        {
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            Value = value
        };
        return attributeDto;
    }
}