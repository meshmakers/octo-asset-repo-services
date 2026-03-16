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
        var cells = row.ColumnNames.Select(columnName =>
        {
            // Standard fields are top-level properties, not in DataPoint.Attributes
            // Column names are camelCase (aligned with CkTypeQueryColumnCollector convention)
            object? value = columnName switch
            {
                "rtId" => row.RtId,
                "ckTypeId" => row.CkTypeId,
                "timestamp" => row.Timestamp,
                "rtWellKnownName" => row.RtWellKnownName,
                "rtCreationDateTime" => row.DataPoint.RtCreationDateTime,
                "rtChangedDateTime" => row.DataPoint.RtChangedDateTime,
                _ => GetAttributeValue(row.DataPoint, columnName)
            };

            return new RtQueryCellDto
            {
                AttributePath = columnName,
                Value = value
            };
        });

        return ConnectionUtils.ToOctoConnection(cells, context);
    }

    private static object? GetAttributeValue(DataPointDto dataPoint, string columnName)
    {
        object? value = null;
        dataPoint.Attributes?.TryGetValue(columnName, out value);
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
}
