using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for a single aggregation spec inside <see cref="CreateRollupArchiveInputType"/>.
/// Mirrors <see cref="CkRollupAggregationSpec"/> from Runtime.Contracts.
/// </summary>
internal sealed class RollupAggregationInputType : InputObjectGraphType<RollupAggregationInputDto>
{
    public RollupAggregationInputType()
    {
        Name = "RollupAggregationInput";
        Description = "One aggregation: source-path on the source archive plus the aggregation function and optional explicit column name.";

        Field<NonNullGraphType<StringGraphType>>("sourcePath")
            .Description("Attribute path on the source archive (must resolve against its captured Columns at activation time).");

        Field<NonNullGraphType<CkRollupFunctionGraphType>>("function")
            .Description("Aggregation function (AVG materialises as two columns: {base}_sum and {base}_count).");

        Field<StringGraphType>("targetColumnName")
            .Description("Optional explicit storage column name. Null falls back to '{sourcePath}_{function}' lower-cased.");

        Field<StringGraphType>("comparisonValue")
            .Description("State literal a STATE_DURATION aggregation matches the source column against — a number ('2', '100'), a boolean ('true'/'false') or a string state name. Required for STATE_DURATION; ignored for every other function.");
    }
}

/// <summary>
/// GraphQL enum for <see cref="CkRollupFunction"/>. Uses the C# enum names directly (AVG, MIN, …)
/// so the constant-case convention matches CK-runtime enums elsewhere in the schema.
/// </summary>
internal sealed class CkRollupFunctionGraphType : EnumerationGraphType<CkRollupFunction>
{
    public CkRollupFunctionGraphType()
    {
        Name = "CkRollupFunction";
        Description = "Aggregation function for a rollup. AVG is stored as two columns (sum + count) so chained rollups stay numerically correct.";
    }
}
