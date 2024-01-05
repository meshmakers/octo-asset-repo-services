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
public sealed class CkRecordDtoType : ObjectGraphType<CkRecordDto>
{
    public CkRecordDtoType()
    {
        Name = "CkRecord";
        Description = "A construction kit record";

        Field(x => x.CkRecordId, type: typeof(IdGraphType)).Description("Unique id of the object.");
        Field(x => x.IsAbstract);
        Field(x => x.IsFinal);

        Connection<CkTypeAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);

        Connection<CkTypeDtoType>("derivedRecordTypes")
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
                    if (ckCacheService == null)
                    {
                        throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
                    }

                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    var result = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId).DerivedRecords
                        .Select(k => ckCacheService.GetCkRecord(graphQlContext.TenantId, k.InheritorCkRecordId));
                    return ConnectionUtils.ToConnection(result.Select(CreateCkRecordDto), ctx, null);
                }
            );

        Field<CkTypeDtoType>("baseRecordTypes").Resolve(ctx =>
        {
            var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
            if (ckCacheService == null)
            {
                throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
            }

            var graphQlContext = (GraphQlUserContext)ctx.UserContext;

            var result = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId).DerivedFromCkRecordId;
            if (result == null)
            {
                return null;
            }

            return CreateCkRecordDto(ckCacheService.GetCkRecord(graphQlContext.TenantId, result.Value));
        });
    }

    private object ResolveAttributes(IResolveConnectionContext<CkRecordDto> ctx)
    {
        var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var graphQlContext = (GraphQlUserContext)ctx.UserContext;

        ctx.TryGetArgument(Statics.AttributeNamesFilterArg,
            out IEnumerable<string>? filterAttributeNames);

        var entityCacheItem = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId);

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

    internal static CkRecordDto CreateCkRecordDto(CkRecordGraph ckRecord)
    {
        var ckRecordDto = new CkRecordDto
        {
            CkRecordId = ckRecord.CkRecordId,
            IsFinal = ckRecord.IsFinal,
            IsAbstract = ckRecord.IsAbstract
        };
        return ckRecordDto;
    }

    internal static CkRecordDto CreateCkRecordDto(CkRecord ckEntity)
    {
        var ckRecordDto = new CkRecordDto
        {
            CkRecordId = ckEntity.CkRecordId,
            IsFinal = ckEntity.IsFinal,
            IsAbstract = ckEntity.IsAbstract
        };
        return ckRecordDto;
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