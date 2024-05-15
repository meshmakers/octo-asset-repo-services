using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class ModelStateDtoType : EnumerationGraphType<ModelState>
{
    public ModelStateDtoType()
    {
        Name = "ModelState";
        Description = "Enum of the availability states of models.";
    }
}