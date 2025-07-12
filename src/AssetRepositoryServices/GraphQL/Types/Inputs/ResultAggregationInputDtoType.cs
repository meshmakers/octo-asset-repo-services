using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class ResultAggregationInputDtoType : InputObjectGraphType<ResultAggregationInputDto>
{
    public ResultAggregationInputDtoType()
    {
        Name = "ResultAggregationInput";
        Field(x => x.GroupBy, typeof(FieldGroupByAggregationInputDtoType));
        Field(x => x.CountAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MinValueAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.MaxValueAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.AvgAttributePaths, typeof(ListGraphType<StringGraphType>));
        Field(x => x.SumAttributePaths, typeof(ListGraphType<StringGraphType>));
    }
}