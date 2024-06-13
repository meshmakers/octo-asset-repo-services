using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkEnumDtoType : ObjectGraphType<CkEnumDto>
{
    public CkEnumDtoType()
    {
        Name = "CkEnum";
        Description = AssetTexts.Graphql_Enum_Description;

        Field(x => x.CkEnumId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkEnumId>>))
            .Description(AssetTexts.Graphql_Enum_CkEnumId_Description);
        Field(x => x.Description, nullable: true)
            .Description(AssetTexts.Graphql_Enum_Description_Description);
        Field(x => x.UseFlags, type: typeof(NonNullGraphType<BooleanGraphType>))
            .Description(AssetTexts.Graphql_Enum_UseFlags_Description);
        Field(x => x.Values, type: typeof(NonNullGraphType<ListGraphType<CkEnumValueDtoType>>))
            .Description(AssetTexts.Graphql_Enum_Values_Description);
    }
    
    internal static CkEnumDto CreateCkEnumDto(CkEnumGraph ckEnumGraph)
    {
        var ckEnumDto = new CkEnumDto
        {
            CkEnumId = ckEnumGraph.CkEnumId,
            Description = ckEnumGraph.Description,
            UseFlags = ckEnumGraph.UseFlags,
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
            Values = ckEnum.Values.Select(CkEnumValueDtoType.CreateCkEnumValueDto).ToList()
        };
        return ckEnumDto;
    }
}