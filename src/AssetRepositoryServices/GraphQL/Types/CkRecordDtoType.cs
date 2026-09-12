using AssetRepositoryServices.Resources;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using CkRecordDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkRecordDto;
using CkTypeAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeAttributeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkRecordDtoType : ObjectGraphType<CkRecordDto>
{
    public CkRecordDtoType()
    {
        Name = "CkRecord";
        Description = AssetTexts.Graphql_Record_Description;

        Field(x => x.CkRecordId, typeof(NonNullGraphType<CkIdGraph<CkRecordId>>))
            .Description(AssetTexts.Graphql_Record_CkRecordId_Description);
        Field(x => x.RtCkRecordId, typeof(NonNullGraphType<RtCkIdGraph<CkRecordId>>))
            .Description(AssetTexts.Graphql_Record_RtCkRecordId_Description);
        Field(x => x.IsAbstract).Description(AssetTexts.Graphql_Record_IsAbstract_Description);
        Field(x => x.IsFinal).Description(AssetTexts.Graphql_Record_IsFinal_Description);
        Field(x => x.Description, true).Description(AssetTexts.Graphql_Record_Description_Description);

        Connection<CkTypeAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg,
                AssetTexts.Graphql_Record_Filter_Attributes_Description)
            .Resolve(ResolveAttributes);

        Connection<CkRecordDtoType>("derivedRecordTypes")
            .Description(AssetTexts.Graphql_Record_DerivedRecords_Description)
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.GetCkCacheService();
                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    var result = ckCacheService.GetCkRecord(graphQlContext.TenantId, ctx.Source.CkRecordId)
                        .DerivedRecords
                        .Select(k => ckCacheService.GetCkRecord(graphQlContext.TenantId, k.InheritorCkRecordId));
                    return ConnectionUtils.ToOctoConnection(result.Select(CreateCkRecordDto), ctx);
                }
            );

        Field<CkRecordDtoType>("baseRecordTypes")
            .Description(AssetTexts.Graphql_Record_BaseRecord_Description)
            .Resolve(ctx =>
            {
                var ckCacheService = ctx.GetCkCacheService();
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
        var ckCacheService = ctx.GetCkCacheService();
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

        // AB#5191: the compiled graph keeps only the RESOLVED ownership per assignment, so the declared
        // per-assignment overrides are read back from the declaring scopes - this record first, then its base
        // records, because the connection returns inherited assignments too.
        var declaringScopes = new List<CkTypeWithAttributesGraph> { ckRecordGraph };
        foreach (var baseRecord in ckRecordGraph.BaseRecords.OrderBy(b => b.BaseTypeDepthIndex))
        {
            if (ckCacheService.TryGetCkRecord(graphQlContext.TenantId, baseRecord.BaseCkRecordId,
                    out var baseRecordGraph))
            {
                declaringScopes.Add(baseRecordGraph);
            }
        }

        var declaredOwnershipOverrides = CkOwnershipUtils.CollectDeclaredOwnershipOverrides(declaringScopes);

        return ConnectionUtils.ToOctoConnection(
            resultList.Select(a => CreateCkTypeAttributeDto(a, declaredOwnershipOverrides, ckCacheService,
                graphQlContext.TenantId)), ctx);
    }

    internal static CkRecordDto CreateCkRecordDto(CkRecordGraph ckRecord)
    {
        var ckRecordDto = new CkRecordDto
        {
            CkRecordId = ckRecord.CkRecordId,
            RtCkRecordId = ckRecord.CkRecordId.ToRtCkId(),
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
            RtCkRecordId = ckEntity.CkRecordId.ToRtCkId(),
            Description = ckEntity.Description,
            IsFinal = ckEntity.IsFinal,
            IsAbstract = ckEntity.IsAbstract
        };
        return ckRecordDto;
    }

    private static CkTypeAttributeDto CreateCkTypeAttributeDto(CkTypeAttributeGraph ckTypeAttributeGraph,
        IReadOnlyDictionary<CkId<CkAttributeId>, AttributeOwnershipDto> declaredOwnershipOverrides,
        ICkCacheService ckCacheService, string tenantId)
    {
        var ownershipOverride = CkOwnershipUtils.GetDeclaredOwnershipOverride(declaredOwnershipOverrides,
            ckTypeAttributeGraph.CkAttributeId);

        // Without an override the assignment's effective ownership IS the definition's, so the definition
        // only has to be looked up in the rare overridden case.
        var definitionOwnership = ownershipOverride == null
            ? ckTypeAttributeGraph.Ownership
            : ckCacheService.GetCkAttribute(tenantId, ckTypeAttributeGraph.CkAttributeId).Ownership;

        var ckEntityAttributeDto = new OwnershipAwareCkTypeAttributeDto
        {
            Ownership = ckTypeAttributeGraph.Ownership,
            OwnershipOverride = ownershipOverride,
            CkAttributeId = ckTypeAttributeGraph.CkAttributeId,
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            AttributeValueType = ckTypeAttributeGraph.ValueType,
            AutoIncrementReference = ckTypeAttributeGraph.AutoIncrementReference,
            AutoCompleteValues = ckTypeAttributeGraph.AutoCompleteValues,
            Attribute = CkAttributeDtoType.CreateCkAttributeDto(ckTypeAttributeGraph, definitionOwnership)
        };
        return ckEntityAttributeDto;
    }
}