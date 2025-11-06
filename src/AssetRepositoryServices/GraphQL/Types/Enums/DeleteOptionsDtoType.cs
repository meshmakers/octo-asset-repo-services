using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class DeleteOptionsDtoType: EnumerationGraphType<DeleteStrategies>
{
    public DeleteOptionsDtoType()
    {
        Name = "DeleteStrategies";
        Description = "Defines possible delete strategies of a runtime type";
    }
}