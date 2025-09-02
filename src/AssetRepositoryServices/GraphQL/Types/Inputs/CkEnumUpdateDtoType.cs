using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class CkEnumUpdateDtoType : InputObjectGraphType<CkEnumUpdateDto>
{
    public CkEnumUpdateDtoType()
    {
        Name = "CkEnumUpdate";
        Field(x => x.Operation, typeof(CkExtensionUpdateOperationsDtoType));
        Field(x => x.Value, typeof(CkEnumValueDtoInputType));
    }

    internal static CkEnumUpdate CreateCkEnumValueDto(CkEnumUpdateDto ckEnumUpdateDto)
    {
        var ckEnumValueDto = new CkEnumUpdate
        {
            Operation = ckEnumUpdateDto.Operation,
            Value = CkEnumValueDtoInputType.CreateCkEnumValueDto(ckEnumUpdateDto.Value)
        };
        return ckEnumValueDto;
    }
}