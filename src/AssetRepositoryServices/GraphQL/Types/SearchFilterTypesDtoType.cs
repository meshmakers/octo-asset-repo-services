using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class SearchFilterTypesDtoType : EnumerationGraphType<SearchFilterTypesDto>
{
    public SearchFilterTypesDtoType()
    {
        Name = "SearchFilterTypes";
        Description =
            "The type of search that is used (a text based search using text analysis (high performance, scoring, maybe more false positives) or filtering of attributes (lower performance, more exact results)";
    }
}