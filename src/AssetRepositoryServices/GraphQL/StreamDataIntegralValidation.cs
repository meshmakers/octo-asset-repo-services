using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// AB#7919: request checks for the <see cref="AggregationFunction.Integral"/> aggregation that the engine (AB#7918)
/// does not make itself. Without them a non-numeric column reaches CrateDB as <c>SUM(text * long)</c> and comes back
/// as an uncoded "An error occurred", and a downsampling request would build an unknown SQL function. Both are client
/// input errors, so they are refused up front with <c>MODEL_VALIDATION_ERRORS</c> and a message that names the column.
/// The missing From/To check lives in the engine (structured <c>StreamDataException</c>) and is not repeated here.
/// </summary>
internal static class StreamDataIntegralValidation
{
    private static readonly HashSet<AttributeValueTypesDto> NumericValueTypes =
    [
        AttributeValueTypesDto.Integer,
        AttributeValueTypesDto.Integer64,
        AttributeValueTypesDto.Double
    ];

    private static readonly HashSet<FormulaResultType> NumericFormulaResultTypes =
    [
        FormulaResultType.Int,
        FormulaResultType.Int64,
        FormulaResultType.Double
    ];

    /// <summary>
    /// Refuses Integral on a column that is not numeric: a built-in default field (rtId, timestamp, ...), a CK
    /// attribute whose value type is not Integer, Integer64 or Double, an archive computed column whose result type
    /// is not Int, Int64 or Double, or any other name without a CK attribute (the platform fields
    /// <c>row_origin</c>, <c>manual_reason</c>, ... are text). Unknown names are left to
    /// <see cref="StreamDataFieldValidation"/>.
    /// </summary>
    /// <param name="fieldResolver">The resolver built for the queried archive.</param>
    /// <param name="archiveSnapshot">The queried archive; its computed columns carry their result type.</param>
    /// <param name="aggregationColumns">The requested aggregation columns.</param>
    /// <param name="typeQueryColumns">Loads the CK attribute columns of the archive's target type; called only when
    /// an Integral column is requested.</param>
    public static void EnsureNumericColumns(
        StreamDataFieldResolver fieldResolver,
        ArchiveSnapshot archiveSnapshot,
        IEnumerable<AggregationColumn> aggregationColumns,
        Func<IReadOnlyCollection<CkTypeQueryColumn>> typeQueryColumns)
    {
        var integralColumns = aggregationColumns.Where(c => c.Function == AggregationFunction.Integral).ToList();
        if (integralColumns.Count == 0)
        {
            return;
        }

        var ckColumns = typeQueryColumns();
        var offending = new List<string>();
        foreach (var column in integralColumns)
        {
            var resolved = fieldResolver.Resolve(column.AttributePath);
            if (resolved == null)
            {
                continue;
            }

            if (resolved.Category == StreamDataFieldCategory.Default)
            {
                offending.Add($"{column.AttributePath} (built-in field)");
                continue;
            }

            var ckColumn = ckColumns.FirstOrDefault(c =>
                string.Equals(c.Path, column.AttributePath, StringComparison.OrdinalIgnoreCase));
            if (ckColumn != null)
            {
                if (!NumericValueTypes.Contains(ckColumn.ValueType))
                {
                    offending.Add($"{column.AttributePath} ({ckColumn.ValueType})");
                }

                continue;
            }

            var computed = archiveSnapshot.Columns.FirstOrDefault(c =>
                c.IsComputed && string.Equals(c.Name, column.AttributePath, StringComparison.OrdinalIgnoreCase));
            if (computed == null)
            {
                offending.Add($"{column.AttributePath} (not a numeric archive column)");
            }
            else if (computed.ResultType is { } resultType && !NumericFormulaResultTypes.Contains(resultType))
            {
                offending.Add($"{column.AttributePath} (computed {resultType})");
            }
        }

        if (offending.Count > 0)
        {
            throw OctoGraphQLException.IntegralRequiresNumericColumns(offending);
        }
    }

    /// <summary>
    /// Refuses Integral in a downsampling request. The engine serves the integral over the whole query window
    /// (aggregation and grouping queries); a per-bucket integral is not implemented.
    /// </summary>
    public static void RejectInDownsampling(IEnumerable<AggregationColumn> aggregationColumns)
    {
        var integral = aggregationColumns.FirstOrDefault(c => c.Function == AggregationFunction.Integral);
        if (integral != null)
        {
            throw OctoGraphQLException.IntegralNotSupportedInDownsampling(integral.AttributePath);
        }
    }
}
