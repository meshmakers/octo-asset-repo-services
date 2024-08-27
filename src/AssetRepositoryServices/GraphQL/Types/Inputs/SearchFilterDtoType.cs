using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class SearchFilterDtoType : InputObjectGraphType<SearchFilterDto>
{
    public SearchFilterDtoType()
    {
        Name = "SearchFilter";
        Field(x => x.Language, true);
        Field(x => x.SearchTerm);
        Field(x => x.Type, typeof(SearchFilterTypesDtoType));
        Field(x => x.AttributeNames, typeof(ListGraphType<StringGraphType>));
    }
}