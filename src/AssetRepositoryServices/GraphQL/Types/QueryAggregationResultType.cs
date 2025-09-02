using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class QueryAggregationResultType : ObjectGraphType<QueryAggregationResult>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public QueryAggregationResultType()
    {
        Name = "QueryAggregationResult";
        Description = "Aggregation query result of items";

        Field(x => x.GroupBy, typeof(ListGraphType<NonNullGraphType<FieldAggregationResultType>>))
            .Description("The grouping input for the aggregation operation.");
        Field(x => x.Count, typeof(NonNullGraphType<IntGraphType>)).Description("The count of entities in the group.");
        Field(x => x.CountStatistics, typeof(ListGraphType<StatisticsResultType>))
            .Description("The count of value of the given attribute paths that are not null.");
        Field(x => x.MinStatistics, typeof(ListGraphType<StatisticsResultType>))
            .Description("The minimum value of the given attribute paths.");
        Field(x => x.MaxStatistics, typeof(ListGraphType<StatisticsResultType>))
            .Description("The maximum value of the given attribute paths.");
        Field(x => x.AvgStatistics, typeof(ListGraphType<StatisticsResultType>))
            .Description("The average value of the given attribute paths.");
        Field(x => x.SumStatistics, typeof(ListGraphType<StatisticsResultType>))
            .Description("The sum value of the given attribute paths.");
    }
}