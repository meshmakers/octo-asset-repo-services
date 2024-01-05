using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class CkEnumValueDtoType : ObjectGraphType<CkEnumValueDto>
{
    public CkEnumValueDtoType()
    {
        Name = "CkEnumValue";
        Description = "A construction kit enum value";

        Field(x => x.Key, type: typeof(IntGraphType))
            .Description("Key of the enum");
        Field(x => x.Name, type: typeof(StringGraphType))
            .Description("Value of the enum");
        Field(x => x.Description, type: typeof(StringGraphType))
            .Description("Description of the enum");
    }
    
    internal static CkEnumValueDto CreateCkEnumValueDto(ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto ckEnumValue)
    {
        var ckEnumValueDto = new CkEnumValueDto
        {
            Key = ckEnumValue.Key,
            Name = ckEnumValue.Name,
            Description = ckEnumValue.Description
        };
        return ckEnumValueDto;
    }
    
    internal static CkEnumValueDto CreateCkEnumValueDto(CkEnumValue ckEnumValue)
    {
        var ckEnumValueDto = new CkEnumValueDto
        {
            Key = ckEnumValue.Key,
            Name = ckEnumValue.Name,
            Description = ckEnumValue.Description
        };
        return ckEnumValueDto;
    }
}