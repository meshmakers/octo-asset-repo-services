using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class GroupingType : ObjectGraphType<GroupingDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public GroupingType()
    {
        Name = "grouping";
        Description = "Grouping of items result";
        Field<NonNullGraphType<ListGraphType<StringGraphType>>>("groupByAttributeNames").Description("A list of attributes the items are grouped by.");

        Field(x => x.Keys, type: typeof(NonNullGraphType<ListGraphType<SimpleScalarType>>)).Description("The key value of the group.");
        Field(x => x.Count, type: typeof(NonNullGraphType<IntGraphType>)).Description("The count of entities in the group.");
        Field(x => x.CountStatistics, type: typeof(ListGraphType<StatisticsType>)).Description("The count of value of the given attribute names that are not null.");
        Field(x => x.MinStatistics, type: typeof(ListGraphType<StatisticsType>)).Description("The minimum value of the given attribute names.");
        Field(x => x.MaxStatistics, type: typeof(ListGraphType<StatisticsType>)).Description("The maximum value of the given attribute names.");
        Field(x => x.AvgStatistics, type: typeof(ListGraphType<StatisticsType>)).Description("The average value of the given attribute names.");
    }
}