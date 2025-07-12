using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class AggregationResultType : ObjectGraphType<AggregationResult>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public AggregationResultType()
    {
        Name = "aggregation";
        Description = "Aggregation result of items";

        Field(x => x.Count, type: typeof(NonNullGraphType<IntGraphType>)).Description("The count of entities in the group.");
        Field(x => x.CountStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The count of value of the given attribute paths that are not null.");
        Field(x => x.MinStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The minimum value of the given attribute paths.");
        Field(x => x.MaxStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The maximum value of the given attribute paths.");
        Field(x => x.AvgStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The average value of the given attribute paths.");
        Field(x => x.SumStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The sum value of the given attribute paths.");
    }
}