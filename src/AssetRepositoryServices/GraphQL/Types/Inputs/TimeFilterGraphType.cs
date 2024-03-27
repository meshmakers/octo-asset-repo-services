using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class TimeFilterGraphType : InputObjectGraphType<EntityTimeFilterDto>
{
    public TimeFilterGraphType()
    {
        Name = "TimeFilter";
        Field(x => x.From);
        Field(x => x.To);
        Field(x => x.Interval, true);
        Field(x => x.Limit, true);
    }
}

internal sealed class AggregationGraphType : EnumerationGraphType<AggregationType>
{
    public AggregationGraphType()
    {
        Name = "AggregationType";
        Description = "Defines the aggregation type";
    }
}

/// <summary>
/// The aggregation type
/// </summary>
public enum AggregationType
{
    /// <summary>
    /// Calculate the minimum
    /// </summary>
    Min,
    /// <summary>
    /// Calculate the maximum
    /// </summary>
    Max,
    /// <summary>
    /// Calculate the average
    /// </summary>
    Average
}


/// <summary>
/// The input type for filtering by time
/// </summary>
public class EntityTimeFilterDto
{
    /// <summary>
    /// Starting time
    /// </summary>
    public DateTime From { get; set; }
    
    /// <summary>
    /// End Time
    /// </summary>
    public DateTime To { get; set; }
    
    /// <summary>
    /// The interval for the aggregation
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// The limit for the aggregation. Default is 100
    /// </summary>
    public int? Limit { get; set; } = 100;
}