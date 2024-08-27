using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class FieldGroupByType : InputObjectGraphType<FieldGroupByDto>
{
    public FieldGroupByType()
    {
        Name = "GroupBy";
        Field(x => x.GroupByAttributeNameList, typeof(NonNullGraphType<ListGraphType<StringGraphType>>));
        Field(x => x.CountAttributeNameList, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MinValueAttributeNameList, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MaxValueAttributeNameList, typeof(ListGraphType<StringGraphType>));
        Field(x => x.AvgAttributeNameList, typeof(ListGraphType<StringGraphType>));
    }
}