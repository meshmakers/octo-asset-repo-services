using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class GlobalQueryOptionsDtoType : InputObjectGraphType<GlobalQueryOptionsDto>
{
    public GlobalQueryOptionsDtoType()
    {
        Name = "GlobalQueryOptions";
        Field(x => x.IncludeArchivedEntities, true);
    }
}