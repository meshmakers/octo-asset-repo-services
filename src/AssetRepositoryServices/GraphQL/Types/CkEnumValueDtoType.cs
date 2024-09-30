using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkEnumValueDtoType : ObjectGraphType<CkEnumValueDto>
{
    public CkEnumValueDtoType()
    {
        Name = "CkEnumValue";
        Description = AssetTexts.Graphql_EnumValue_Description;

        Field(x => x.Key, type: typeof(IntGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Key_Description);
        Field(x => x.Name, type: typeof(StringGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Name_Description);
        Field(x => x.Description, type: typeof(StringGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Description_Description);
        Field(x => x.IsExtension, type: typeof(BooleanGraphType))
            .Description(AssetTexts.Graphql_EnumValue_IsExtension_Description);
    }
    
    internal static CkEnumValueDto CreateCkEnumValueDto(ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto ckEnumValue)
    {
        var ckEnumValueDto = new CkEnumValueDto
        {
            Key = ckEnumValue.Key,
            Name = ckEnumValue.Name,
            Description = ckEnumValue.Description,
            IsExtension = ckEnumValue.IsExtension
        };
        return ckEnumValueDto;
    }
    
    internal static CkEnumValueDto CreateCkEnumValueDto(CkEnumValue ckEnumValue)
    {
        var ckEnumValueDto = new CkEnumValueDto
        {
            Key = ckEnumValue.Key,
            Name = ckEnumValue.Name,
            Description = ckEnumValue.Description,
            IsExtension = ckEnumValue.IsExtension
        };
        return ckEnumValueDto;
    }
}