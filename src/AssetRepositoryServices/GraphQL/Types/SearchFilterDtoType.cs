using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class SearchFilterDtoType : InputObjectGraphType<SearchFilterDto>
{
    public SearchFilterDtoType()
    {
        Name = "SearchFilter";
        Field(x => x.Language, true);
        Field(x => x.SearchTerm);
        Field(x => x.Type, true, typeof(SearchFilterTypesDtoType));
        Field(x => x.AttributeNames, true, typeof(ListGraphType<StringGraphType>));
    }
}
