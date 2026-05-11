using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Construction kit attributes Graph QL type definition
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkAttributeDtoType : ObjectGraphType<CkAttributeDto>
{
    /// <inheritdoc />
    public CkAttributeDtoType()
    {
        Name = "CkAttribute";
        Description = "Construction kit attribute definitions";

        Field(x => x.CkAttributeId, typeof(NonNullGraphType<CkIdGraph<CkAttributeId>>))
            .Description("Construction kit attribute id.");
        Field(x => x.AttributeValueType, typeof(NonNullGraphType<AttributeValueTypesDtoType>))
            .Description("Value type of the attribute.");
        Field<CkRecordDtoType>("CkRecord")
            .Description("Optional record id of the attribute value type.")
            .Resolve(ResolveCkRecord);
        Field<CkEnumDtoType>("CkEnum")
            .Description("Optional enum id of the attribute value type.")
            .Resolve(ResolveCkEnum);
        Field(x => x.Description, typeof(StringGraphType))
            .Description("Optional description of the attribute.");
        Field(x => x.MetaData, typeof(ListGraphType<CkAttributeMetaDataDtoType>))
            .Description("Optional meta data of the attribute.");
        Field<ListGraphType<SimpleScalarType>, object>(nameof(CkAttributeDto.DefaultValues))
            .Description("Default values of the attribute.");
    }

    private object? ResolveCkEnum(IResolveFieldContext<CkAttributeDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (arg.Source.ValueCkEnumId == null)
        {
            return null;
        }

        var ckEnumGraph = ckCacheService.GetCkEnum(graphQlUserContext.TenantId, arg.Source.ValueCkEnumId);
        return CkEnumDtoType.CreateCkEnumDto(ckEnumGraph);
    }

    private object? ResolveCkRecord(IResolveFieldContext<CkAttributeDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (arg.Source.ValueCkRecordId == null)
        {
            return null;
        }

        var ckRecordGraph = ckCacheService.GetCkRecord(graphQlUserContext.TenantId, arg.Source.ValueCkRecordId);
        return CkRecordDtoType.CreateCkRecordDto(ckRecordGraph);
    }


    internal static CkAttributeDto CreateCkAttributeDto(CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var attributeDto = new CkAttributeDto
        {
            CkAttributeId = ckTypeAttributeGraph.CkAttributeId,
            AttributeValueType = ckTypeAttributeGraph.ValueType,
            ValueCkRecordId = ckTypeAttributeGraph.ValueCkRecordId,
            ValueCkEnumId = ckTypeAttributeGraph.ValueCkEnumId,
            Description = ckTypeAttributeGraph.Description,
            MetaData = ckTypeAttributeGraph.MetaData?.Select(CkAttributeMetaDataDtoType.CreateCkAttributeMetaDataDto)
                .ToList(),
            DefaultValues = ckTypeAttributeGraph.DefaultValues
        };

        return attributeDto;
    }

    internal static CkAttributeDto CreateCkAttributeDto(CkAttribute ckAttribute)
    {
        var attributeDto = new CkAttributeDto
        {
            CkAttributeId = ckAttribute.CkAttributeId,
            AttributeValueType = ckAttribute.AttributeValueType,
            ValueCkRecordId = ckAttribute.ValueCkRecordId,
            ValueCkEnumId = ckAttribute.ValueCkEnumId,
            Description = ckAttribute.Description,
            DefaultValues = ckAttribute.DefaultValues
        };

        return attributeDto;
    }
}