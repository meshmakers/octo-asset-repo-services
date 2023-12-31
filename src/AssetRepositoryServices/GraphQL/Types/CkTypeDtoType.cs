using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class CkTypeDtoType : ObjectGraphType<CkTypeDto>
{
    public CkTypeDtoType()
    {
        Name = "CkType";
        Description = "A construction kit type";

        Field(x => x.CkTypeId, type: typeof(IdGraphType)).Description("Unique id of the object.");
        Field(x => x.IsAbstract);
        Field(x => x.IsFinal);

        Connection<CkTypeAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);

        Connection<CkTypeDtoType>("derivedTypes")
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
                    if (ckCacheService == null)
                    {
                        throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
                    }
                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    var result = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId).DerivedTypes
                        .Select(k => ckCacheService.GetCkType(graphQlContext.TenantId, k.InheritorCkTypeId));
                    return ConnectionUtils.ToConnection(result.Select(CreateCkTypeDto), ctx, null);
                }
            );

        Field<CkTypeDtoType>("baseType").Resolve(ctx =>
        {
            var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
            if (ckCacheService == null)
            {
                throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
            }
            var graphQlContext = (GraphQlUserContext)ctx.UserContext;

            var result = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId).DerivedFromCkTypeId;
            if (result == null)
            {
                return null;
            }

            return CreateCkTypeDto(ckCacheService.GetCkType(graphQlContext.TenantId, result.Value));
        });
    }

    private object ResolveAttributes(IResolveConnectionContext<CkTypeDto> ctx)
    {
        var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
        
        var graphQlContext = (GraphQlUserContext)ctx.UserContext;

        ctx.TryGetArgument(Statics.AttributeNamesFilterArg, 
            out IEnumerable<string>? filterAttributeNames);

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

        return ConnectionUtils.ToConnection(resultList.Select(CreateCkTypeAttributeDto), ctx, null);
    }

    internal static CkTypeDto CreateCkTypeDto(CkTypeGraph entityCacheItem)
    {
        var ckEntityDto = new CkTypeDto
        {
            CkTypeId = entityCacheItem.CkTypeId,
            IsFinal = entityCacheItem.IsFinal,
            IsAbstract = entityCacheItem.IsAbstract,
        };
        return ckEntityDto;
    }

    internal static CkTypeDto CreateCkTypeDto(CkType ckEntity)
    {
        var ckEntityDto = new CkTypeDto
        {
            CkTypeId = ckEntity.CkTypeId,
            IsFinal = ckEntity.IsFinal,
            IsAbstract = ckEntity.IsAbstract,
        };
        return ckEntityDto;
    }

    private CkTypeAttributeDto CreateCkTypeAttributeDto(CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var ckEntityAttributeDto = new CkTypeAttributeDto
        {
            CkAttributeId = ckTypeAttributeGraph.CkAttributeId,
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            AttributeValueType = ckTypeAttributeGraph.ValueType,
            AutoIncrementReference = ckTypeAttributeGraph.AutoIncrementReference,
            AutoCompleteValues = ckTypeAttributeGraph.AutoCompleteValues,
            Attribute = CkAttributeDtoType.CreateCkAttributeDto(ckTypeAttributeGraph)
        };
        return ckEntityAttributeDto;
    }
}
