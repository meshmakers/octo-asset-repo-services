using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

public sealed class SortDtoType : InputObjectGraphType<SortDto>
{
    public SortDtoType()
    {
        Name = "Sort";
        Field(x => x.SortOrder, type: typeof(SortOrdersDtoType));
        Field(x => x.AttributeName);
    }
}