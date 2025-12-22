using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class FieldGroupByAggregationInputDtoType : InputObjectGraphType<FieldGroupByAggregationInputDto>
{
    public FieldGroupByAggregationInputDtoType()
    {
        Name = "FieldGroupByAggregationInput";
        Field(x => x.GroupByAttributePaths, typeof(NonNullGraphType<ListGraphType<StringGraphType>>));
        Field(x => x.CountAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MinValueAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MaxValueAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.AvgAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.SumAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.ResolveEnumValuesToNames, typeof(BooleanGraphType))
            .Description("When true, enum integer values in groupBy keys are resolved to their label names. Defaults to true.")
            .DefaultValue(true);
    }
}