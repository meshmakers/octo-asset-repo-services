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
}

internal sealed class StreamDataQueryColumnInputDtoType : InputObjectGraphType<StreamDataQueryColumnInputDto>
{
    public StreamDataQueryColumnInputDtoType()
    {
        Name = "StreamDataQueryColumnInput";
        Field(x => x.AggregationType, typeof(NonNullGraphType<AggregationGraphType>));
        Field(x => x.AttributePath, typeof(NonNullGraphType<StringGraphType>));
    }
}
