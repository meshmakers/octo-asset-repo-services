using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Represents a single row in a stream data query result.
/// Wraps an engine-level <see cref="StreamDataRow"/> for GraphQL exposure.
/// </summary>
internal sealed class StreamDataQueryRowDto : GraphQlDto
{
    public OctoObjectId RtId { get; set; }

    /// <summary>
    /// CK type of the backing entity. Null for aggregation / grouped-aggregation / downsampling
    /// rows, which have no backing entity — the <see cref="RtCkIdGraph{TCkKey}"/> scalar then
    /// serialises it to GraphQL <c>null</c>. Must stay null in that case: an empty
    /// <c>RtCkId&lt;CkTypeId&gt;("")</c> has a null <c>ElementId</c> and throws a
    /// NullReferenceException from its <c>IsEmpty</c>/<c>SemanticVersionedFullName</c> getter
    /// during serialization (observed as "NULL_REFERENCE: Error trying to resolve field 'ckTypeId'").
    /// </summary>
    public RtCkId<CkTypeId>? CkTypeId { get; set; }
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
            // Leave null for entity-less rows (aggregation/grouping/downsampling) — see CkTypeId
            // remark. Do NOT coalesce to an empty RtCkId: that serialises with a NullReferenceException.
            CkTypeId = row.CkTypeId,
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
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames,
                "When true, enum integer values are resolved to their label names. Defaults to true.")
            .Resolve(ResolveCells);
    }

    private static object ResolveCells(IResolveConnectionContext<StreamDataQueryRowDto> context)
    {
        var row = context.Source;

        // Default to true for parity with the runtime query path (RtQueryRowDtoType).
        context.TryGetArgument(Statics.ResolveEnumValuesToNames, true, out bool resolveEnumValuesToNames);

        // Only touch the CK cache when at least one column is enum-typed and resolution is on.
        var resolveEnums = resolveEnumValuesToNames && row.ColumnNames.Any(m => m.EnumId is not null);
        ICkCacheService? ckCacheService = null;
        string? tenantId = null;
        if (resolveEnums)
        {
            ckCacheService = context.GetCkCacheService();
            tenantId = ((GraphQlUserContext)context.UserContext).TenantId;
        }

        var cells = row.ColumnNames.Select(mapping =>
        {
            row.Values.TryGetValue(mapping.Canonical, out var value);
            if (resolveEnums && mapping.EnumId is { } enumId && value is not null)
            {
                value = ResolveEnumName(ckCacheService!, tenantId!, enumId, value);
            }

            return new RtQueryCellDto
            {
                AttributePath = mapping.Wire,
                Value = value
            };
        });

        return ConnectionUtils.ToOctoConnection(cells, context);
    }

    /// <summary>
    /// Resolves a raw integer enum key to its CK enum value name. Returns the original value
    /// unchanged when it is not an integer key, the enum is not in the cache, or the key has no
    /// matching value — stream-data display must never fail on an unexpected value.
    /// </summary>
    private static object? ResolveEnumName(ICkCacheService ckCacheService, string tenantId,
        CkId<CkEnumId> enumId, object value)
    {
        if (!TryGetEnumKey(value, out var key))
        {
            return value;
        }

        if (!ckCacheService.TryGetCkEnum(tenantId, enumId, out var ckEnumGraph) || ckEnumGraph == null)
        {
            return value;
        }

        var match = ckEnumGraph.Values.FirstOrDefault(v => v.Key == key);
        return match?.Name ?? value;
    }

    /// <summary>
    /// Coerces an integral value (CrateDB may surface enum keys as int or long) to an int key.
    /// Non-integral values (doubles, strings, …) are rejected so they pass through unchanged.
    /// </summary>
    private static bool TryGetEnumKey(object value, out int key)
    {
        switch (value)
        {
            case int i:
                key = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                key = (int)l;
                return true;
            case short s:
                key = s;
                return true;
            case byte b:
                key = b;
                return true;
            default:
                key = 0;
                return false;
        }
    }
}
