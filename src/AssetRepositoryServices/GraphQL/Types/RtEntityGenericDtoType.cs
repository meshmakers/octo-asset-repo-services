using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a generic runtime entities type that can be used for generic access to entities
/// </summary>
public sealed class RtEntityGenericDtoType : ObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityGenericDtoType()
    {
        Name = "RtEntity";
        Description = "A runtime entity type of Octo";
        Field(d => d.RtId, type: typeof(OctoObjectIdType));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);

        Connection<RtEntityAttributeDtoType>()
            .Name("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtEntityDto> ctx)
    {
        var graphQlContext = (GraphQLUserContext)ctx.UserContext;

        var filterAttributeNames = ctx.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

        var entityCacheItem = graphQlContext.TenantContext.CkCache.GetEntityCacheItem(ctx.Source.CkId);

        IEnumerable<AttributeCacheItem> resultList;
        if (filterAttributeNames == null)
        {
            resultList = entityCacheItem.Attributes.Values;
        }
        else
        {
            resultList =
                entityCacheItem.Attributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtEntityAttributeDto((RtEntity)ctx.Source.UserContext, item)),
            ctx);
    }

    private RtEntityAttributeDto CreateRtEntityAttributeDto(RtEntity rtEntity,
        AttributeCacheItem attributeCacheItem)
    {
        var attributeDto = new RtEntityAttributeDto
        {
            AttributeName = attributeCacheItem.AttributeName.ToCamelCase(),
            Value = rtEntity.GetAttributeValueOrDefault(attributeCacheItem.AttributeName),
            UserContext = attributeCacheItem
        };
        return attributeDto;
    }
}
