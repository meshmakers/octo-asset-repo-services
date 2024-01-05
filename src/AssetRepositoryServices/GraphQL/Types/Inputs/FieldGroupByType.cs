using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class FieldGroupByType : InputObjectGraphType<FieldGroupByDto>
{
    public FieldGroupByType()
    {
        Name = "GroupBy";
        Field(x => x.GroupByAttributeNameList, false, typeof(NonNullGraphType<ListGraphType<StringGraphType>>));
        Field(x => x.CountAttributeNameList, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MinValueAttributeNameList, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MaxValueAttributeNameList, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.AvgAttributeNameList, false, typeof(ListGraphType<StringGraphType>));
    }
}