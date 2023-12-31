using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class UpdateTypesDtoType : EnumerationGraphType<UpdateTypesDto>
{
    public UpdateTypesDtoType()
    {
        Name = "UpdateType";
        Description = "Enum of valid update types";
    }
}
