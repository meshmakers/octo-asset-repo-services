using GraphQL.Types;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Input DTO for a stream data aggregation column.
/// </summary>
public class StreamDataQueryColumnInputDto
{
    /// <summary>
    ///     The aggregation function to apply.
    /// </summary>
    public AggregationFunctionDto AggregationType { get; set; }

    /// <summary>
    ///     The attribute path to aggregate.
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     State literal a STATE_DURATION column matches the attribute against — a number ('2'),
    ///     a boolean ('true'/'false') or a string state name. Required for STATE_DURATION;
    ///     ignored for every other aggregation type. AB#4336 / AB#4341.
    /// </summary>
    public string? ComparisonValue { get; set; }
}

internal sealed class StreamDataQueryColumnInputDtoType : InputObjectGraphType<StreamDataQueryColumnInputDto>
{
    public StreamDataQueryColumnInputDtoType()
    {
        Name = "StreamDataQueryColumnInput";
        Field(x => x.AggregationType, typeof(NonNullGraphType<AggregationGraphType>));
        Field(x => x.AttributePath, typeof(NonNullGraphType<StringGraphType>));
        Field(x => x.ComparisonValue, typeof(StringGraphType))
            .Description("State literal a STATE_DURATION column matches the attribute against (number / true / false / string). Required for STATE_DURATION; ignored otherwise.");
    }
}
