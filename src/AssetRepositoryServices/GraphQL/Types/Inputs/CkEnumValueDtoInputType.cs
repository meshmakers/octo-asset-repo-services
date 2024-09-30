using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class CkEnumValueDtoInputType : InputObjectGraphType<CkEnumValueDto>
{
    public CkEnumValueDtoInputType()
    {
        Name = "CkEnumValueInput";
        Description = AssetTexts.Graphql_EnumValue_Description;

        Field(x => x.Key, type: typeof(IntGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Key_Description);
        Field(x => x.Name, type: typeof(StringGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Name_Description);
        Field(x => x.Description, type: typeof(StringGraphType))
            .Description(AssetTexts.Graphql_EnumValue_Description_Description);
    }

    internal static Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto CreateCkEnumValueDto(
        CkEnumValueDto ckEnumValue)
    {
        var ckEnumValueDto = new Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto
        {
            Key = ckEnumValue.Key,
            Name = ckEnumValue.Name,
            Description = ckEnumValue.Description,
            IsExtension = ckEnumValue.IsExtension
        };
        return ckEnumValueDto;
    }
}