using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.StreamData.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Represents a single row in a persisted stream data query result.
/// </summary>
internal sealed class StreamDataQueryRowDto : GraphQlDto
{
    public OctoObjectId RtId { get; set; }
    public RtCkId<CkTypeId> CkTypeId { get; set; } = null!;
    public DateTime? Timestamp { get; set; }
    public string? RtWellKnownName { get; set; }

    /// <summary>
    ///     Column names selected in the query.
    /// </summary>
    public required IReadOnlyList<string> ColumnNames { get; init; }

    /// <summary>
    ///     The underlying data point with attribute values.
    /// </summary>
    public required DataPointDto DataPoint { get; init; }

    /// <summary>
    ///     Optional mapping from SQL aliases to original attribute paths (for aggregation queries).
    /// </summary>
    public Dictionary<string, string>? AliasToOriginalPath { get; init; }
}

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class StreamDataQueryRowDtoType : ObjectGraphType<StreamDataQueryRowDto>
{
    public StreamDataQueryRowDtoType()
    {
        Name = "StreamDataQueryRow";
        Description = "A single row in a persisted stream data query result.";

        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(d => d.Timestamp, typeof(DateTimeGraphType));
        Field(d => d.RtWellKnownName, true);

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Description("The data cells for this row, one per selected column.")
            .Resolve(ResolveCells);
    }

    private static object ResolveCells(IResolveConnectionContext<StreamDataQueryRowDto> context)
    {
        var row = context.Source;
        var isAggregation = row.AliasToOriginalPath != null;
        var cells = row.ColumnNames.Select(columnName =>
        {
            // For aggregation results, all values come from the Attributes dictionary
            // (standard field names like "rtWellKnownName" are aggregated, not row-level)
            // For simple results, standard fields are top-level properties
            object? value;
            if (isAggregation)
            {
                value = GetAttributeValue(row, columnName);
            }
            else
            {
                value = columnName switch
                {
                    "rtId" => row.RtId,
                    "ckTypeId" => row.CkTypeId,
                    "timestamp" => row.Timestamp,
                    "rtWellKnownName" => row.RtWellKnownName,
                    "rtCreationDateTime" => row.DataPoint.RtCreationDateTime,
                    "rtChangedDateTime" => row.DataPoint.RtChangedDateTime,
                    _ => GetAttributeValue(row, columnName)
                };
            }

            return new RtQueryCellDto
            {
                AttributePath = columnName,
                Value = value
            };
        });

        return ConnectionUtils.ToOctoConnection(cells, context);
    }

    private static object? GetAttributeValue(StreamDataQueryRowDto row, string columnName)
    {
        object? value = null;

        // For aggregation queries, the DataPoint attributes use SQL aliases (e.g., "Count_voltage")
        // but the cell attributePath uses the original name (e.g., "voltage").
        // Try the reverse mapping first to find the alias key in the attributes dictionary.
        if (row.AliasToOriginalPath != null)
        {
            var aliasKey = row.AliasToOriginalPath
                .FirstOrDefault(kvp => kvp.Value == columnName).Key;
            if (aliasKey != null)
            {
                row.DataPoint.Attributes?.TryGetValue(aliasKey, out value);
                return value;
            }
        }

        row.DataPoint.Attributes?.TryGetValue(columnName, out value);
        return value;
    }

    internal static StreamDataQueryRowDto CreateFromDataPoint(DataPointDto dataPoint, IReadOnlyList<string> columnNames)
    {
        return new StreamDataQueryRowDto
        {
            RtId = dataPoint.RtId ?? throw OctoGraphQLException.RtIdUndefined(),
            CkTypeId = dataPoint.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            Timestamp = dataPoint.Timestamp,
            RtWellKnownName = dataPoint.RtWellKnownName,
            ColumnNames = columnNames,
            DataPoint = dataPoint
        };
    }

    /// <summary>
    ///     Creates a row DTO from an aggregation result. Aggregation rows may not have
    ///     standard entity fields (RtId, CkTypeId, etc.) — values come from the Attributes dictionary.
    ///     The aliasToOriginalPath mapping remaps SQL aliases (e.g., "Count_voltage") back to
    ///     original attribute paths (e.g., "voltage") so the cell attributePaths match what the
    ///     frontend expects — consistent with how runtime aggregation queries work.
    /// </summary>
    internal static StreamDataQueryRowDto CreateFromDataPointForAggregation(
        DataPointDto dataPoint,
        IReadOnlyList<string> columnNames,
        Dictionary<string, string>? aliasToOriginalPath = null)
    {
        return new StreamDataQueryRowDto
        {
            RtId = dataPoint.RtId ?? OctoObjectId.Empty,
            CkTypeId = dataPoint.CkTypeId ?? new RtCkId<CkTypeId>(""),
            Timestamp = dataPoint.Timestamp,
            RtWellKnownName = dataPoint.RtWellKnownName,
            ColumnNames = columnNames,
            DataPoint = dataPoint,
            AliasToOriginalPath = aliasToOriginalPath
        };
    }
}
