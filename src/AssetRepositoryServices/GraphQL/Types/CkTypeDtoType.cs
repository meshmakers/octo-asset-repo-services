using AssetRepositoryServices.Resources;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using CkTypeAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeAttributeDto;
using CkTypeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkTypeDtoType : ObjectGraphType<CkTypeDto>
{
    public CkTypeDtoType()
    {
        Name = "CkType";
        Description = AssetTexts.Graphql_Type_Description;

        Field(x => x.CkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>))
            .Description(AssetTexts.Graphql_Type_CkTypeId_Description);
        Field(x => x.IsAbstract).Description(AssetTexts.Graphql_Type_IsAbstract_Description);
        Field(x => x.IsFinal).Description(AssetTexts.Graphql_Type_IsFinal_Description);
        Field(x => x.Description, true).Description(AssetTexts.Graphql_Type_Description_Description);

        Connection<CkTypeAttributeDtoType>("attributes")
            .Argument<StringGraphType>(Statics.AttributeNameContainsFilterArg,
                AssetTexts.Graphql_Type_Filter_AttributeNameContainsFilter_Description)
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg,
                AssetTexts.Graphql_Type_Filter_Attributes_Description)
            .Resolve(ResolveAttributes);

        Connection<CkTypeQueryColumnDtoType>("availableQueryColumns")
            .Argument<StringGraphType>(Statics.AttributePathContainsFilterArg,
                AssetTexts.Graphql_Type_Filter_AttributePathContainsFilter_Description)
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Type_Filter_AttributePaths_Description)
            .Resolve(ResolveAvailableQueryColumns);

        Connection<CkTypeDtoType>("derivedTypes")
            .Description(AssetTexts.Graphql_Type_DerivedTypes_Description)
            .Argument<BooleanGraphType>(Statics.IgnoreAbstractTypesArg,
                AssetTexts.Graphql_Type_Filter_IgnoreAbstractTypes_Description)
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.GetCkCacheService();
                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    if (!ctx.TryGetArgument(Statics.IgnoreAbstractTypesArg,
                            out bool? ignoreAbstractTypes))
                    {
                        ignoreAbstractTypes = false;
                    }

                    var result = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId).DerivedTypes
                        .Select(k => ckCacheService.GetCkType(graphQlContext.TenantId, k.InheritorCkTypeId))
                        .Where(t => !t.IsAbstract || !ignoreAbstractTypes.Value);
                    return ConnectionUtils.ToConnection(result.Select(CreateCkTypeDto), ctx);
                }
            );

        Connection<CkTypeDtoType>("directAndIndirectDerivedTypes")
            .Description(AssetTexts.Graphql_Type_DirectAndIndirectDerivedTypes_Description)
            .Argument<BooleanGraphType>(Statics.IgnoreAbstractTypesArg,
                AssetTexts.Graphql_Type_Filter_IgnoreAbstractTypes_Description)
            .Argument<BooleanGraphType>(Statics.IncludeSelfArgs,
                AssetTexts.Graphql_Type_Filter_IncludeSelfArgs_Description)
            .Resolve(ctx =>
                {
                    var ckCacheService = ctx.GetCkCacheService();
                    var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                    if (!ctx.TryGetArgument(Statics.IgnoreAbstractTypesArg,
                            out bool? ignoreAbstractTypes))
                    {
                        ignoreAbstractTypes = false;
                    }

                    if (!ctx.TryGetArgument(Statics.IncludeSelfArgs,
                            out bool? includeSelf))
                    {
                        includeSelf = false;
                    }

                    var result = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId)
                        .GetAllDerivedTypes(includeSelf.Value)
                        .Select(derivedCkTypeId => ckCacheService.GetCkType(graphQlContext.TenantId, derivedCkTypeId))
                        .Where(t => !t.IsAbstract || !ignoreAbstractTypes.Value);
                    return ConnectionUtils.ToConnection(result.Select(CreateCkTypeDto), ctx);
                }
            );

        Field<CkTypeDtoType>("baseType")
            .Description(AssetTexts.Graphql_Type_BaseType_Description)
            .Resolve(ctx =>
            {
                var ckCacheService = ctx.GetCkCacheService();
                var graphQlContext = (GraphQlUserContext)ctx.UserContext;

                var result = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId).DerivedFromCkTypeId;
                if (result == null)
                {
                    return null;
                }

                return CreateCkTypeDto(ckCacheService.GetCkType(graphQlContext.TenantId, result));
            });
    }

    private object ResolveAvailableQueryColumns(IResolveConnectionContext<CkTypeDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)arg.UserContext;

        arg.TryGetArgument(Statics.AttributePathsFilterArg,
            out IEnumerable<string>? filterAttributePaths);
        arg.TryGetArgument(Statics.AttributePathContainsFilterArg,
            out string? attributePathContainsFilter);

        var resultList =
            ckCacheService.GetCkTypeQueryColumnPaths(graphQlContext.TenantId, arg.Source.CkTypeId)
                .Select(CreateCkTypeQueryColumnDto).ToList();

        if (filterAttributePaths != null)
        {
            resultList = resultList.Where(a =>
                filterAttributePaths.Select(f => f.ToLower()).Contains(a.AttributePath.ToLower())).ToList();
        }

        if (!string.IsNullOrWhiteSpace(attributePathContainsFilter))
        {
            resultList =
                resultList.Where(a => a.AttributePath.ToLower().Contains(attributePathContainsFilter.ToLower()))
                    .ToList();
        }


        return ConnectionUtils.ToConnection(resultList.OrderBy(a => a.AttributePath), arg);
    }

    private object ResolveAttributes(IResolveConnectionContext<CkTypeDto> ctx)
    {
        var ckCacheService = ctx.GetCkCacheService();
        var graphQlContext = (GraphQlUserContext)ctx.UserContext;

        ctx.TryGetArgument(Statics.AttributeNamesFilterArg,
            out IEnumerable<string>? filterAttributeNames);
        ctx.TryGetArgument(Statics.AttributeNameContainsFilterArg,
            out string? attributeNameContainsFilter);

        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, ctx.Source.CkTypeId);

        var resultList = ckTypeGraph.AllAttributes.Values.ToList();
        if (filterAttributeNames != null)
        {
            resultList = resultList.Where(a =>
                filterAttributeNames.Contains(a.AttributeName.ToCamelCase())).ToList();
        }

        if (!string.IsNullOrWhiteSpace(attributeNameContainsFilter))
        {
            resultList =
                resultList.Where(a => a.AttributeName.ToLower().Contains(attributeNameContainsFilter))
                    .ToList();
        }


        return ConnectionUtils.ToConnection(resultList.Select(CreateCkTypeAttributeDto), ctx);
    }

    internal static CkTypeDto CreateCkTypeDto(CkTypeGraph ckTypeGraph)
    {
        var ckEntityDto = new CkTypeDto
        {
            CkTypeId = ckTypeGraph.CkTypeId,
            Description = ckTypeGraph.Description,
            IsFinal = ckTypeGraph.IsFinal,
            IsAbstract = ckTypeGraph.IsAbstract
        };
        return ckEntityDto;
    }

    internal static CkTypeDto CreateCkTypeDto(CkType ckEntity)
    {
        var ckEntityDto = new CkTypeDto
        {
            CkTypeId = ckEntity.CkTypeId,
            Description = ckEntity.Description,
            IsFinal = ckEntity.IsFinal,
            IsAbstract = ckEntity.IsAbstract
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
            IsOptional = ckTypeAttributeGraph.IsOptional,
            Attribute = CkAttributeDtoType.CreateCkAttributeDto(ckTypeAttributeGraph)
        };
        return ckEntityAttributeDto;
    }

    private CkTypeQueryColumnDto CreateCkTypeQueryColumnDto(CkTypeQueryColumn ckTypeQueryColumn)
    {
        var ckTypeQueryColumnDto = new CkTypeQueryColumnDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            AttributeValueType = ckTypeQueryColumn.ValueType
        };
        return ckTypeQueryColumnDto;
    }
}