using GraphQL.Types;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;

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

internal sealed class SortOrderDtoGraphType : EnumerationGraphType<SortOrderDto>
{
    public SortOrderDtoGraphType()
    {
        Name = "SortOrder";
        Description = "Defines the sort order";
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
    /// The limit for the aggregation. Default
    /// </summary>
    public int? Limit { get; set; }
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
        Field(x => x.SortOrder, type: typeof(SortOrderDtoGraphType));
        Field(x => x.SortPriority, true, typeof(IntGraphType))
            .Description(" Defines the priority of the sort. Lower values are sorted first; null values aren't sorted at all.");
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
    public AggregationFunctionDto? AggregationType { get; set; }
    
    /// <summary>
    /// Defines the priority of the sort. Lower values are sorted first; null values aren't sorted at all.
    /// When two entities have the same priority value, order cannot be guaranteed. 
    /// </summary>
    public int? SortPriority { get; set; }

    /// <summary>
    /// Defines the sort order
    /// </summary>
    public SortOrderDto? SortOrder { get; set; } 
        
}