using System.Collections.Generic;
using System.Linq;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class CkEntityDtoType : ObjectGraphType<CkEntityDto>
{
    public CkEntityDtoType()
    {
        Name = "CkEntity";
        Description = "A construction kit type";

        Field(x => x.CkId, type: typeof(IdGraphType)).Description("Unique id of the object.");
        Field(x => x.TypeName);
        Field(x => x.ScopeId, type: typeof(ScopeIdsDtoType));
        Field(x => x.IsAbstract);
        Field(x => x.IsFinal);

        Connection<CkEntityAttributeDtoType>()
            .Name("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);

        Connection<CkEntityDtoType>()
            .Name("derivedTypes")
            .Resolve(ctx =>
                {
                    var graphQlContext = (GraphQLUserContext)ctx.UserContext;

                    var result = graphQlContext.TenantContext.CkCache.GetEntityCacheItem(ctx.Source.CkId).DerivedTypes;
                    return ConnectionUtils.ToConnection(result.Select(CreateCkEntityDto), ctx, null);
                }
            );

        Field<CkEntityDtoType>().Name("baseType").Resolve(ctx =>
        {
            var graphQlContext = (GraphQLUserContext)ctx.UserContext;

            var result = graphQlContext.TenantContext.CkCache.GetEntityCacheItem(ctx.Source.CkId).BaseType;
            if (result == null)
            {
                return null;
            }

            return CreateCkEntityDto(result);
        });
    }

    private object ResolveAttributes(IResolveConnectionContext<CkEntityDto> ctx)
    {
        var graphQlContext = (GraphQLUserContext)ctx.UserContext;

        ctx.TryGetArgument(Statics.AttributeNamesFilterArg, out var hasNamesDefined,
            out IEnumerable<string> filterAttributeNames);

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

        return ConnectionUtils.ToConnection(resultList.Select(CreateCkEntityAttributeDto), ctx, null);
    }

    internal static CkEntityDto CreateCkEntityDto(EntityCacheItem entityCacheItem)
    {
        var ckEntityDto = new CkEntityDto
        {
            CkId = entityCacheItem.CkId,
            TypeName = entityCacheItem.CkId.GetGraphQlName(),
            IsFinal = entityCacheItem.IsFinal,
            IsAbstract = entityCacheItem.IsAbstract,
            ScopeId = (ScopeIdsDto)entityCacheItem.ScopeId
        };
        return ckEntityDto;
    }

    internal static CkEntityDto CreateCkEntityDto(CkEntity ckEntity)
    {
        var ckEntityDto = new CkEntityDto
        {
            CkId = ckEntity.CkId,
            TypeName = ckEntity.CkId.GetGraphQlName(),
            IsFinal = ckEntity.IsFinal,
            IsAbstract = ckEntity.IsAbstract,
            ScopeId = (ScopeIdsDto)ckEntity.ScopeId
        };
        return ckEntityDto;
    }

    private CkEntityAttributeDto CreateCkEntityAttributeDto(AttributeCacheItem attributeCacheItem)
    {
        var ckEntityAttributeDto = new CkEntityAttributeDto
        {
            AttributeId = attributeCacheItem.AttributeId,
            AttributeName = attributeCacheItem.AttributeName.ToCamelCase(),
            AttributeValueType = (AttributeValueTypesDto)attributeCacheItem.AttributeValueType,
            IsAutoCompleteEnabled = attributeCacheItem.IsAutoCompleteEnabled,
            AutoCompleteTexts = attributeCacheItem.AutoCompleteTexts,
            AutoCompleteFilter = attributeCacheItem.AutoCompleteFilter,
            AutoCompleteLimit = attributeCacheItem.AutoCompleteLimit,
            AutoIncrementReference = attributeCacheItem.AutoIncrementReference,
            Attribute = CkAttributeDtoType.CreateCkAttributeDto(attributeCacheItem)
        };
        return ckEntityAttributeDto;
    }
}
