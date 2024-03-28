using GraphQL.Types;
using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class EntityTimeFilterGraphType : InputObjectGraphType<EntityTimeFilterDto>
{
    public EntityTimeFilterGraphType()
    {
        Name = "TimeFilter";
        Field(x => x.From);
        Field(x => x.To);
        Field(x => x.Interval, true);
        Field(x => x.Limit, true);
    }
}

internal sealed class AggregationGraphType : EnumerationGraphType<AggregationFunctionDto>
{
    public AggregationGraphType()
    {
        Name = "AggregationType";
        Description = "Defines the aggregation type";
    }
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


/// <summary>
/// The input type for filtering by attributes
/// </summary>
public sealed class AttributeTsArgumentGraphType : InputObjectGraphType<AttributeTsArgumentDto>
{
    /// <summary>
    /// ctor
    /// </summary>
    public AttributeTsArgumentGraphType()
    {
        Name = "AttributeArgument";
        Field(x => x.AggregationType, type: typeof(AggregationGraphType));
    }
}

/// <summary>
/// The input type for filtering by attributes
/// </summary>
public class AttributeTsArgumentDto
{
    /// <summary>
    /// The aggregation type
    /// </summary>
    public AggregationFunctionDto AggregationType { get; set; }
}