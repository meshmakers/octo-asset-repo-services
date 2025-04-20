using AssetRepositoryServices.Resources;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkRecordDtoType : ObjectGraphType<CkRecordDto>
{
    public CkRecordDtoType()
    {
        Name = "CkRecord";
        Description = AssetTexts.Graphql_Record_Description;

        Field(x => x.CkRecordId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkRecordId>>))
            .Description(AssetTexts.Graphql_Record_CkRecordId_Description);
        Field(x => x.IsAbstract).Description(AssetTexts.Graphql_Record_IsAbstract_Description);
        Field(x => x.IsFinal).Description(AssetTexts.Graphql_Record_IsFinal_Description);
        Field(x => x.Description, nullable: true).Description(AssetTexts.Graphql_Record_Description_Description);

        Connection<CkTypeAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg,
                AssetTexts.Graphql_Record_Filter_Attributes_Description)
            .Resolve(ResolveAttributes);

        Connection<CkTypeDtoType>("derivedRecordTypes")
            .Description(AssetTexts.Graphql_Record_DerivedRecords_Description)
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
                    if (ckCacheService == null)
                    {
                        throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
                    }

                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    var result = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId)
                        .DerivedRecords
                        .Select(k => ckCacheService.GetCkRecord(graphQlContext.TenantId, k.InheritorCkRecordId));
                    return ConnectionUtils.ToConnection(result.Select(CreateCkRecordDto), ctx, null);
                }
            );

        Field<CkTypeDtoType>("baseRecordTypes")
            .Description(AssetTexts.Graphql_Record_BaseRecord_Description)
            .Resolve(ctx =>
            {
                var ckCacheService = ctx.RequestServices?.GetRequiredService<ICkCacheService>();
                if (ckCacheService == null)
                {
                    throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
                }

                var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                var result = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId)
                    .DerivedFromCkRecordId;
                if (result == null)
                {
                    return null;
                }

                return CreateCkRecordDto(ckCacheService.GetCkRecord(graphQlContext.TenantId, result));
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

        var ckRecordGraph = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        if (filterAttributeNames == null)
        {
            resultList = ckRecordGraph.AllAttributes.Values;
        }
        else
        {
            resultList =
                ckRecordGraph.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }

        return ConnectionUtils.ToConnection(resultList.Select(CreateCkTypeAttributeDto), ctx, null);
    }

    internal static CkRecordDto CreateCkRecordDto(CkRecordGraph ckRecord)
    {
        var ckRecordDto = new CkRecordDto
        {
            CkRecordId = ckRecord.CkRecordId,
            Description = ckRecord.Description,
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
            Description = ckEntity.Description,
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