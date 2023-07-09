using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class GroupByFilterDtoType : InputObjectGraphType<FieldGroupByDto>
{
    public GroupByFilterDtoType()
    {
        Name = "GroupBy";
        Field(x => x.AttributeNames, false, typeof(NonNullGraphType<ListGraphType<StringGraphType>>));
        Field(x => x.CountAttributeNames, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MinValueAttributeNames, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MaxValueAttributeNames, false, typeof(ListGraphType<StringGraphType>));
        Field(x => x.AvgAttributeNames, false, typeof(ListGraphType<StringGraphType>));
    }
}
