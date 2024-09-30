using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class CkEnumUpdateDtoType : InputObjectGraphType<CkEnumUpdateDto>
{
    public CkEnumUpdateDtoType()
    {
        Name = "CkEnumUpdate";
        Field(x => x.Operation, type: typeof(CkExtensionUpdateOperationsDtoType));
        Field(x => x.Value, type: typeof(CkEnumValueDtoInputType));
    }
    
    internal static ConstructionKit.Contracts.ModelRepositories.CkEnumUpdate CreateCkEnumValueDto(CkEnumUpdateDto ckEnumUpdateDto)
    {
        var ckEnumValueDto = new ConstructionKit.Contracts.ModelRepositories.CkEnumUpdate 
        {
            Operation = ckEnumUpdateDto.Operation,
            Value = CkEnumValueDtoInputType.CreateCkEnumValueDto(ckEnumUpdateDto.Value),
        };
        return ckEnumValueDto;
    }
}