using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class FieldAggregationResultType : ObjectGraphType<FieldAggregationResult>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public FieldAggregationResultType()
    {
        Name = "fieldAggregation";
        Description = "Field aggregation result of items";
        Field<NonNullGraphType<ListGraphType<StringGraphType>>>("groupByAttributePaths")
            .Description("A list of attributes paths the items are grouped by.");

        Field(x => x.Keys, typeof(NonNullGraphType<ListGraphType<SimpleScalarType>>))
            .Description("The key value of the group.");
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