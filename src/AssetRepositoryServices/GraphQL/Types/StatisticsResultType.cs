using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Defines the statistics type for results
/// </summary>
public sealed class StatisticsResultType : ObjectGraphType<StatisticsResult>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public StatisticsResultType()
    {
        Name = "statistics";
        Description = "Statistics of items result";
        Field(x => x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name of the statistic");
        Field(x => x.Value, type: typeof(SimpleScalarType)).Description("Statistic value");
    }
}