using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Defines the statistics type for results
/// </summary>
public sealed class StatisticsType: ObjectGraphType<StatisticsDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public StatisticsType()
    {
        Name = "statistics";
        Description = "Statistics of items result";
        Field(x=> x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name of the statistic");
        Field(x=> x.Value, type: typeof(SimpleScalarType)).Description("Statistic value");
    }
}