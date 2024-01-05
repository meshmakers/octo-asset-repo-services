using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class GroupingResultType : ObjectGraphType<GroupingResult>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public GroupingResultType()
    {
        Name = "grouping";
        Description = "Grouping of items result";
        Field<NonNullGraphType<ListGraphType<StringGraphType>>>("groupByAttributeNames")
            .Description("A list of attributes the items are grouped by.");

        Field(x => x.Keys, type: typeof(NonNullGraphType<ListGraphType<SimpleScalarType>>)).Description("The key value of the group.");
        Field(x => x.Count, type: typeof(NonNullGraphType<IntGraphType>)).Description("The count of entities in the group.");
        Field(x => x.CountStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The count of value of the given attribute names that are not null.");
        Field(x => x.MinStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The minimum value of the given attribute names.");
        Field(x => x.MaxStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The maximum value of the given attribute names.");
        Field(x => x.AvgStatistics, type: typeof(ListGraphType<StatisticsResultType>))
            .Description("The average value of the given attribute names.");
    }
}