using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkEnumDtoType : ObjectGraphType<CkEnumDto>
{
    public CkEnumDtoType()
    {
        Name = "CkEnum";
        Description = AssetTexts.Graphql_Enum_Description;

        Field(x => x.CkEnumId, typeof(NonNullGraphType<CkIdGraph<CkEnumId>>))
            .Description(AssetTexts.Graphql_Enum_CkEnumId_Description);
        Field(x => x.RtCkEnumId, typeof(NonNullGraphType<RtCkIdGraph<CkEnumId>>))
            .Description(AssetTexts.Graphql_Enum_RtCkEnumId_Description);
        Field(x => x.Description, true)
            .Description(AssetTexts.Graphql_Enum_Description_Description);
        Field(x => x.UseFlags, typeof(NonNullGraphType<BooleanGraphType>))
            .Description(AssetTexts.Graphql_Enum_UseFlags_Description);
        Field(x => x.IsExtensible, typeof(NonNullGraphType<BooleanGraphType>))
            .Description(AssetTexts.Graphql_Enum_IsExtensible_Description);
        Field(x => x.Values, typeof(NonNullGraphType<ListGraphType<CkEnumValueDtoType>>))
            .Description(AssetTexts.Graphql_Enum_Values_Description);
    }

    internal static CkEnumDto CreateCkEnumDto(CkEnumGraph ckEnumGraph)
    {
        var ckEnumDto = new CkEnumDto
        {
            CkEnumId = ckEnumGraph.CkEnumId,
            Description = ckEnumGraph.Description,
            UseFlags = ckEnumGraph.UseFlags,
            IsExtensible = ckEnumGraph.IsExtensible,
            Values = ckEnumGraph.Values.Select(CkEnumValueDtoType.CreateCkEnumValueDto).ToList()
        };
        return ckEnumDto;
    }

    internal static CkEnumDto CreateCkEnumDto(CkEnum ckEnum)
    {
        var ckEnumDto = new CkEnumDto
        {
            CkEnumId = ckEnum.CkEnumId,
            Description = ckEnum.Description,
            UseFlags = ckEnum.UseFlags,
            IsExtensible = ckEnum.IsExtensible,
            Values = ckEnum.Values.Select(CkEnumValueDtoType.CreateCkEnumValueDto).ToList()
        };
        return ckEnumDto;
    }
}