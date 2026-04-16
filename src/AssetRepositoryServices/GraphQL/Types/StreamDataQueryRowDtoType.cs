using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Represents a single row in a stream data query result.
/// Wraps an engine-level <see cref="StreamDataRow"/> for GraphQL exposure.
/// </summary>
internal sealed class StreamDataQueryRowDto : GraphQlDto
{
    public OctoObjectId RtId { get; set; }
    public RtCkId<CkTypeId> CkTypeId { get; set; } = null!;
    public DateTime? Timestamp { get; set; }
    public string? RtWellKnownName { get; set; }
    public DateTime? RtCreationDateTime { get; set; }
    public DateTime? RtChangedDateTime { get; set; }

    /// <summary>
    /// Column names selected in the query, in order. Used to build the cells.
    /// Each entry pairs the canonical (PascalCase) key used to look up values
    /// with the wire (camelCase) name emitted as attributePath.
    /// </summary>
    public required IReadOnlyList<ColumnNameMapping> ColumnNames { get; init; }

    /// <summary>
    /// Values for this row, keyed by the canonical PascalCase column name. Resolved by the engine.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>
    /// Creates a row DTO from an engine-level <see cref="StreamDataRow"/>.
    /// </summary>
    public static StreamDataQueryRowDto FromStreamDataRow(
        StreamDataRow row,
        IReadOnlyList<ColumnNameMapping> columnNames)
    {
        return new StreamDataQueryRowDto
        {
            RtId = row.RtId ?? OctoObjectId.Empty,
            CkTypeId = row.CkTypeId ?? new RtCkId<CkTypeId>(""),
            Timestamp = row.Timestamp,
            RtWellKnownName = row.RtWellKnownName,
            RtCreationDateTime = row.RtCreationDateTime,
            RtChangedDateTime = row.RtChangedDateTime,
            ColumnNames = columnNames,
            Values = row.Values
        };
    }
}

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class StreamDataQueryRowDtoType : ObjectGraphType<StreamDataQueryRowDto>
{
    public StreamDataQueryRowDtoType()
    {
        Name = "StreamDataQueryRow";
        Description = "A single row in a stream data query result.";

        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(d => d.Timestamp, typeof(DateTimeGraphType));
        Field(d => d.RtWellKnownName, true);
        Field(d => d.RtCreationDateTime, typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(DateTimeGraphType));

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Description("The data cells for this row, one per selected column.")
            .Resolve(ResolveCells);
    }

    private static object ResolveCells(IResolveConnectionContext<StreamDataQueryRowDto> context)
    {
        var row = context.Source;
        var cells = row.ColumnNames.Select(mapping =>
        {
            row.Values.TryGetValue(mapping.Canonical, out var value);
            return new RtQueryCellDto
            {
                AttributePath = mapping.Wire,
                Value = value
            };
        });

        return ConnectionUtils.ToOctoConnection(cells, context);
    }
}
