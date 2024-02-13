using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class UpdateTypesDtoType : EnumerationGraphType<UpdateTypesDto>
{
    public UpdateTypesDtoType()
    {
        Name = "UpdateType";
        Description = "Enum of valid update types";
    }
}