using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class SortDtoType : InputObjectGraphType<SortDto>
{
    public SortDtoType()
    {
        Name = "Sort";
        Field(x => x.SortOrder, type: typeof(SortOrdersDtoType));
        Field(x => x.AttributePath);
    }
}