using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     AB#7812: anchor columns of a stream-data query. A column <c>anchor.&lt;attributePath&gt;</c> in the column list of a
///     simple stream-data query shows an attribute of the <b>anchor entity</b> of the row (the runtime entity the row's
///     <c>rtId</c> points at, e.g. the OBIS code and the unit of an energy measurement) next to the archive's measured values.
/// </summary>
/// <remarks>
///     <para>
///         The anchor columns are split off before the stream-data repository is called, so the engine's column validator and
///         the SQL are untouched. Per result page the distinct anchor ids are loaded in <b>one</b> runtime query through the
///         tenant repository with the session of the caller (data permissions apply: an anchor the caller may not read gives
///         null cells and nothing else). Enum keys are resolved to names by the cell resolver of the row through the
///         <see cref="ColumnNameMapping.EnumId" /> of the column, like every other stream-data enum column.
///     </para>
///     <para>
///         v1 is display only: an anchor column in a field filter, a sort order, an aggregation, grouping or downsampling
///         query is refused with a validation error. The value is the <b>current</b> value of the anchor, not the value at the
///         time of the measurement.
///     </para>
/// </remarks>
internal static class StreamDataAnchorColumns
{
    /// <summary>The reserved prefix of an anchor column; the rest is an attribute path of the archive's target CK type.</summary>
    public const string Prefix = "anchor.";

    /// <summary>The limit named in every refusal.</summary>
    internal const string DisplayOnlyLimit =
        "Anchor columns are display-only in v1: they can be listed as columns of a simple stream-data query, " +
        "not used in field filters, sorting, aggregation, grouping or downsampling.";

    /// <summary>True when the column is an anchor column (case-insensitive prefix).</summary>
    public static bool IsAnchor(string? column) =>
        column != null && column.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The attribute path of an anchor column (the column without the prefix).</summary>
    public static string PathOf(string anchorColumn) => anchorColumn[Prefix.Length..];

    /// <summary>
    ///     Refuses the anchor columns among <paramref name="paths" /> with a validation error that names the column and
    ///     the limit. <paramref name="usage" /> says where the column was used ("a field filter", "a sort order", ...).
    /// </summary>
    public static void EnsureNone(IEnumerable<string>? paths, string usage)
    {
        foreach (var path in paths ?? [])
        {
            if (IsAnchor(path))
            {
                throw new OctoGraphQLException(
                    $"Anchor column '{path}' cannot be used in {usage}. {DisplayOnlyLimit}");
            }
        }
    }

    /// <summary>Splits a requested column list and decides which columns the engine has to read (see <see cref="AnchorPlan" />).</summary>
    public static AnchorPlan Plan(IReadOnlyList<string> requestedColumns, StreamDataFieldResolver fieldResolver)
    {
        var anchors = requestedColumns.Where(IsAnchor).ToList();
        if (anchors.Count == 0)
        {
            return AnchorPlan.None(requestedColumns);
        }

        var archiveColumns = requestedColumns.Where(c => !IsAnchor(c)).ToList();
        // The row's rtId is the key to the anchor. The engine reports it with the projected columns; asking for it
        // explicitly (it is a default field of every archive kind) keeps that independent of what else is listed.
        var engineColumns = new List<string>(archiveColumns);
        var rtIdResolved = fieldResolver.Resolve(Constants.RtId);
        if (rtIdResolved != null &&
            !archiveColumns.Any(c => string.Equals(fieldResolver.Resolve(c)?.CrateDbName, rtIdResolved.CrateDbName,
                StringComparison.Ordinal)))
        {
            engineColumns.Add(Constants.RtId);
        }

        return new AnchorPlan(requestedColumns, archiveColumns, engineColumns, anchors);
    }

    /// <summary>
    ///     Checks the anchor columns against the archive's target CK type: every path must be a CK attribute of the type
    ///     (a record sub-attribute included), not Hidden and not a Secret. A path that is none is refused with an error that
    ///     names the column and the CK type.
    /// </summary>
    public static void Validate(AnchorPlan plan, RtCkId<CkTypeId> ckTypeId,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns, StreamDataHiddenGuard hiddenGuard)
    {
        foreach (var column in plan.AnchorColumns)
        {
            var path = PathOf(column);
            hiddenGuard.EnsureNotHidden(path);

            var ck = string.IsNullOrWhiteSpace(path) ? null : StreamDataColumnBuilder.FindCkColumn(path, typeQueryColumns);
            if (ck == null)
            {
                throw new OctoGraphQLException(
                    $"Invalid anchor column '{column}': '{path}' is not an attribute of the CK type '{ckTypeId}'.");
            }

            if (ck.ValueType == AttributeValueTypesDto.Secret)
            {
                throw new OctoGraphQLException(
                    $"Anchor column '{column}' refers to a secret attribute of the CK type '{ckTypeId}' and cannot be shown.");
            }
        }
    }

    /// <summary>
    ///     The column metadata of a requested column: the CK value type for an anchor column (unknown paths are marked
    ///     unknown, like an unknown archive column, so listing a query never fails; running it does), the regular column
    ///     otherwise.
    /// </summary>
    public static RtQueryColumnDto BuildColumn(string column,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns, StreamDataFieldResolver fieldResolver)
    {
        if (!IsAnchor(column))
        {
            return StreamDataColumnBuilder.BuildSimpleColumn(column, typeQueryColumns, fieldResolver);
        }

        var ck = StreamDataColumnBuilder.FindCkColumn(PathOf(column), typeQueryColumns);
        if (ck == null || ck.ValueType == AttributeValueTypesDto.Secret)
        {
            return new RtQueryColumnDto
            {
                AttributePath = column,
                AttributeValueType = AttributeValueTypesDto.String,
                AggregationType = AggregationTypesDto.None,
                UserContext = UnknownStreamDataColumn.Instance
            };
        }

        return new RtQueryColumnDto
        {
            AttributePath = column,
            AttributeValueType = ck.ValueType,
            AggregationType = AggregationTypesDto.None
        };
    }

    /// <summary>
    ///     Resolves the anchors of one page and builds its rows: one batched load of the distinct anchor ids
    ///     (<paramref name="loadAnchors" /> is called at most once, never for a page without anchor columns or ids),
    ///     the requested attribute paths projected into the row values under the requested column as key.
    /// </summary>
    /// <param name="rows">The rows of the page.</param>
    /// <param name="mappings">The column mappings of the full requested column list (see <see cref="AnchorPlan.MergeMappings" />).</param>
    /// <param name="plan">The plan of the query.</param>
    /// <param name="loadAnchors">Loads the anchor entities by id, with the rights of the caller; ids that are not returned are not visible.</param>
    /// <param name="readValue">Reads the value of an attribute path from an anchor entity.</param>
    public static async Task<IReadOnlyList<StreamDataQueryRowDto>> BuildRowsAsync(
        IReadOnlyList<StreamDataRow> rows,
        IReadOnlyList<ColumnNameMapping> mappings,
        AnchorPlan plan,
        Func<IReadOnlyList<OctoObjectId>, Task<IReadOnlyDictionary<OctoObjectId, RtEntity>>> loadAnchors,
        Func<RtEntity, string, object?> readValue)
    {
        if (!plan.HasAnchors)
        {
            return rows.Select(r => StreamDataQueryRowDto.FromStreamDataRow(r, mappings)).ToList();
        }

        var ids = rows
            .Select(r => r.RtId)
            .Where(id => id is { } v && v != OctoObjectId.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<OctoObjectId, RtEntity> anchors = ids.Count == 0
            ? new Dictionary<OctoObjectId, RtEntity>()
            : await loadAnchors(ids).ConfigureAwait(false);

        return rows.Select(row =>
        {
            var values = new Dictionary<string, object?>(row.Values);
            RtEntity? anchor = null;
            if (row.RtId is { } id && anchors.TryGetValue(id, out var found))
            {
                anchor = found;
            }

            foreach (var column in plan.AnchorColumns)
            {
                values[column] = anchor == null ? null : readValue(anchor, PathOf(column));
            }

            return StreamDataQueryRowDto.FromStreamDataRow(row, mappings, values);
        }).ToList();
    }

    /// <summary>
    ///     Production wiring of <c>BuildRowsAsync</c>: the anchors are read with the session of the request (bound to
    ///     the caller's security context) in one call of the tenant repository.
    /// </summary>
    public static Task<IReadOnlyList<StreamDataQueryRowDto>> BuildPageRowsAsync(
        IResolveFieldContext context, GraphQlUserContext gql, RtCkId<CkTypeId> ckTypeId,
        IReadOnlyList<StreamDataRow> rows, IReadOnlyList<ColumnNameMapping> mappings, AnchorPlan plan)
    {
        if (!plan.HasAnchors)
        {
            return BuildRowsAsync(rows, mappings, plan, _ => throw new InvalidOperationException(), (_, _) => null);
        }

        var repository = gql.TenantContext.GetTenantRepository();
        var session = context.GetSessionAccessor().Session;
        var ckCache = context.GetCkCacheService();
        var tenantId = gql.TenantId;

        return BuildRowsAsync(rows, mappings, plan,
            async ids =>
            {
                var result = await repository
                    .GetRtEntitiesByIdAsync(session, ckTypeId, ids, RtEntityQueryOptions.Create(), 0, ids.Count)
                    .ConfigureAwait(false);
                return (IReadOnlyDictionary<OctoObjectId, RtEntity>)result.Items
                    .GroupBy(e => e.RtId)
                    .ToDictionary(g => g.Key, g => g.First());
            },
            (entity, path) => RtPathEvaluator.GetValue(ckCache, tenantId, entity, path));
    }
}

/// <summary>
///     What a simple stream-data query asks of the engine and of the anchor load: the requested column list in order, its
///     archive part, the list the engine reads (the archive part plus <c>rtid</c> when anchors need it) and the anchor
///     columns.
/// </summary>
internal sealed class AnchorPlan
{
    public AnchorPlan(IReadOnlyList<string> requestedColumns, IReadOnlyList<string> archiveColumns,
        IReadOnlyList<string> engineColumns, IReadOnlyList<string> anchorColumns)
    {
        RequestedColumns = requestedColumns;
        ArchiveColumns = archiveColumns;
        EngineColumns = engineColumns;
        AnchorColumns = anchorColumns;
    }

    /// <summary>The plan of a query without anchor columns: everything goes to the engine as before.</summary>
    public static AnchorPlan None(IReadOnlyList<string> requestedColumns) =>
        new(requestedColumns, requestedColumns, requestedColumns, []);

    /// <summary>The columns as requested, in order.</summary>
    public IReadOnlyList<string> RequestedColumns { get; }

    /// <summary>The requested columns without the anchor columns.</summary>
    public IReadOnlyList<string> ArchiveColumns { get; }

    /// <summary>The columns the stream-data repository is asked for.</summary>
    public IReadOnlyList<string> EngineColumns { get; }

    /// <summary>The anchor columns as requested (with the prefix).</summary>
    public IReadOnlyList<string> AnchorColumns { get; }

    public bool HasAnchors => AnchorColumns.Count > 0;

    /// <summary>
    ///     Mappings for the requested columns in the requested order: the archive mappings (one per archive column, in
    ///     order) with the anchor columns put back at their place. An anchor column's key and wire name are the requested
    ///     column; its enum id comes from the attribute path.
    /// </summary>
    public IReadOnlyList<ColumnNameMapping> MergeMappings(
        IReadOnlyList<ColumnNameMapping> archiveMappings, Func<string, CkId<CkEnumId>?> enumIdResolver)
    {
        if (!HasAnchors)
        {
            return archiveMappings;
        }

        var merged = new List<ColumnNameMapping>(RequestedColumns.Count);
        var next = 0;
        foreach (var column in RequestedColumns)
        {
            merged.Add(StreamDataAnchorColumns.IsAnchor(column)
                ? new ColumnNameMapping(column, column, enumIdResolver(StreamDataAnchorColumns.PathOf(column)))
                : archiveMappings[next++]);
        }

        return merged;
    }
}
