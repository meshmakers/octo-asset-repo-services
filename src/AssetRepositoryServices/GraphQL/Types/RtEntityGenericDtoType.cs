using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

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

        Connection<RtEntityAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtEntityDto> ctx)
    {
        var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var graphQlContext = (GraphQlUserContext)ctx.UserContext;

        var filterAttributeNames = ctx.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

        var entityCacheItem = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        if (filterAttributeNames == null)
        {
            resultList = entityCacheItem.AllAttributes.Values;
        }
        else
        {
            resultList =
                entityCacheItem.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtEntityAttributeDto((RtEntity)ctx.Source.UserContext!, item)),
            ctx, null);
    }

    private RtEntityAttributeDto CreateRtEntityAttributeDto(RtEntity rtEntity,
        CkTypeAttributeGraph attributeCacheItem)
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