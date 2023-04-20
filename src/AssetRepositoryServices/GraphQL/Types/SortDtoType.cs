using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class SortDtoType : InputObjectGraphType<SortDto>
{
    public SortDtoType()
    {
        Name = "Sort";
        Field(x => x.SortOrder, type: typeof(SortOrdersDtoType));
        Field(x => x.AttributeName);
    }
}
