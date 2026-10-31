using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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

    /// <summary>
    /// Refuses Integral on a column that is not numeric: a built-in default field (rtId, timestamp, ...) or a CK
    /// attribute whose value type is not Integer, Integer64 or Double. A data-stream column without a CK attribute
    /// (an archive computed column such as <c>ActivePower &gt; 0 ? ActivePower : 0</c>) is numeric by construction
    /// and passes. Unknown names are left to <see cref="StreamDataFieldValidation"/>.
    /// </summary>
    /// <param name="fieldResolver">The resolver built for the queried archive.</param>
    /// <param name="aggregationColumns">The requested aggregation columns.</param>
    /// <param name="typeQueryColumns">Loads the CK attribute columns of the archive's target type; called only when
    /// an Integral column is requested.</param>
    public static void EnsureNumericColumns(
        StreamDataFieldResolver fieldResolver,
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
            if (ckColumn != null && !NumericValueTypes.Contains(ckColumn.ValueType))
            {
                offending.Add($"{column.AttributePath} ({ckColumn.ValueType})");
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
