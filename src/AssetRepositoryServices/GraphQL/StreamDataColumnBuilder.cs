using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Marker in <see cref="GraphQlDto.UserContext" /> of a stream-data column whose path is neither an attribute of the
///     archive's CK type nor a field the archive knows (default, platform, computed or rollup column). Such a column is
///     reported with <c>attributeValueType: null</c> (AB#8030) instead of a made-up <c>STRING</c>.
/// </summary>
internal sealed class UnknownStreamDataColumn
{
    public static readonly UnknownStreamDataColumn Instance = new();

    private UnknownStreamDataColumn()
    {
    }
}

/// <summary>
///     Builds the column metadata (<see cref="RtQueryColumnDto" />) of the persisted (<c>streamDataQuery</c>) and the
///     transient stream-data queries.
/// </summary>
/// <remarks>
///     AB#8030: the attribute path is matched against the CK type's query columns <b>case-insensitively</b>, the same rule
///     the query execution applies (<see cref="StreamDataFieldResolver" /> uses <c>OrdinalIgnoreCase</c>) and the widgets
///     use. Before, the exact match failed for PascalCase paths (<c>ActivePower</c> against the CK path <c>activePower</c>)
///     and the column was typed <c>STRING</c>, so every numeric picker in the MeshBoard dialogs stayed empty. A path that
///     is not a CK attribute keeps <c>STRING</c> when the archive knows it (e.g. <c>timestamp</c>, <c>rtId</c>,
///     <c>window_start</c>, <c>row_origin</c>, rollup and computed columns; unchanged behaviour) and is marked
///     <see cref="UnknownStreamDataColumn" /> otherwise, which the GraphQL type reports as <c>null</c>.
/// </remarks>
internal static class StreamDataColumnBuilder
{
    /// <summary>
    ///     Finds the CK query column of <paramref name="attributePath" />: an exact match first, then a case-insensitive one.
    /// </summary>
    public static CkTypeQueryColumn? FindCkColumn(
        string attributePath,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns)
    {
        return typeQueryColumns.FirstOrDefault(c => string.Equals(c.Path, attributePath, StringComparison.Ordinal))
               ?? typeQueryColumns.FirstOrDefault(c =>
                   string.Equals(c.Path, attributePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Builds a simple (non-aggregating) column, also used for grouping columns.
    /// </summary>
    /// <param name="attributePath">The path as stored in the query or requested by the client; echoed verbatim.</param>
    /// <param name="typeQueryColumns">Query columns of the archive's CK type (no navigation properties).</param>
    /// <param name="fieldResolver">The archive's field resolver; decides whether a non-CK path is known.</param>
    public static RtQueryColumnDto BuildSimpleColumn(
        string attributePath,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns,
        StreamDataFieldResolver fieldResolver)
    {
        return BuildColumn(attributePath, AggregationTypesDto.None, typeQueryColumns, fieldResolver);
    }

    /// <summary>
    ///     Builds an aggregation column with the result type of the aggregation function (Count -> Integer, Average and
    ///     Integral -> Double, ...; see <see cref="GetAggregationResultType" />). An unknown path stays unknown for every
    ///     function: the query over it cannot run.
    /// </summary>
    public static RtQueryColumnDto BuildAggregationColumn(
        string attributePath,
        AggregationTypesDto aggregationType,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns,
        StreamDataFieldResolver fieldResolver)
    {
        return BuildColumn(attributePath, aggregationType, typeQueryColumns, fieldResolver);
    }

    /// <summary>
    ///     Result type of an aggregation over a column of <paramref name="sourceType" />. Mirrors
    ///     <c>RtQueryColumnType.GetAggregationResultType</c> plus the stream-data-only functions.
    /// </summary>
    public static AttributeValueTypesDto GetAggregationResultType(
        AttributeValueTypesDto sourceType,
        AggregationTypesDto aggregationType)
    {
        return aggregationType switch
        {
            AggregationTypesDto.None => sourceType,
            AggregationTypesDto.Count => AttributeValueTypesDto.Integer,
            AggregationTypesDto.Sum => sourceType,
            AggregationTypesDto.Average => AttributeValueTypesDto.Double,
            AggregationTypesDto.Minimum => sourceType,
            AggregationTypesDto.Maximum => sourceType,
            // Same result types as the transient path: ms-in-state is an integer duration (AB#4341),
            // the integral is value x hours (AB#7919).
            AggregationTypesDto.StateDuration => AttributeValueTypesDto.Integer,
            AggregationTypesDto.Integral => AttributeValueTypesDto.Double,
            _ => sourceType
        };
    }

    private static RtQueryColumnDto BuildColumn(
        string attributePath,
        AggregationTypesDto aggregationType,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns,
        StreamDataFieldResolver fieldResolver)
    {
        var ck = FindCkColumn(attributePath, typeQueryColumns);
        if (ck == null && fieldResolver.Resolve(attributePath) == null)
        {
            // The value is never read: RtQueryColumnType reports the marker as null.
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.String,
                AggregationType = aggregationType,
                UserContext = UnknownStreamDataColumn.Instance
            };
        }

        var sourceType = ck?.ValueType ?? AttributeValueTypesDto.String;
        return new RtQueryColumnDto
        {
            AttributePath = attributePath,
            AttributeValueType = GetAggregationResultType(sourceType, aggregationType),
            AggregationType = aggregationType
        };
    }
}
