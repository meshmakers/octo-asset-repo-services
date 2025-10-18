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
        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
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
        var ckCacheService = context.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)context.UserContext;


        var ckTypeGraph = ckCacheService.GetRtCkType(graphQlContext.TenantId, context.Source.CkTypeId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        IEnumerable<string>? filterAttributeNames = null;
        if (context.HasArgument(Statics.AttributeNamesFilterArg))
        {
            filterAttributeNames = context.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

            resultList =
                ckTypeGraph.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }
        else
        {
            resultList = ckTypeGraph.AllAttributes.Values;
        }

        context.TryGetArgument(Statics.ResolveEnumValuesToNames, out bool resolveEnumValuesToNames);

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtEntityAttributeDto(ckCacheService, graphQlContext.TenantId,
                (RtEntity)context.Source.UserContext!, item, resolveEnumValuesToNames, filterAttributeNames)),
            context);
    }

    internal static RtEntityAttributeDto CreateRtEntityAttributeDto(ICkCacheService ckCacheService, string tenantId,
        RtTypeWithAttributes rtEntity,
        CkTypeAttributeGraph ckTypeAttributeGraph, bool resolveEnumValuesToNames,
        IEnumerable<string>? filterAttributeNames = null)
    {
        var value = rtEntity.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName);

        if (value is RtRecord rtRecord)
        {
            value = RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, tenantId, rtRecord,
                resolveEnumValuesToNames,
                filterAttributeNames?.ToArray());
        }
        else if (value is IEnumerable<object> rtRecordCandidates)
        {
            value = rtRecordCandidates.Select(listValue =>
            {
                if (listValue is RtRecord rtRecord2)
                {
                    return RtRecordDtoType.CreateRtRecordDtoWithAttributes(ckCacheService, tenantId, rtRecord2,
                        resolveEnumValuesToNames, filterAttributeNames?.ToArray());
                }

                return listValue;
            });
        }

        if (resolveEnumValuesToNames)
        {
            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Enum &&
                ckTypeAttributeGraph.ValueCkEnumId != null && value != null)
            {
                var ckEnumGraph = ckCacheService.GetCkEnum(tenantId, ckTypeAttributeGraph.ValueCkEnumId);
                if (value is IEnumerable<object> enumValues)
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