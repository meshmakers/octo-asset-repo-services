using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL type that wraps <see cref="StreamDataQueryDto"/>.
/// Exposes three scalar fields (<c>queryRtId</c>, <c>associatedCkTypeId</c>, <c>columns</c>)
/// plus two sub-connections:
/// <list type="bullet">
///   <item><c>Rows</c> — dispatches to the correct stream-data engine call based on the
///     runtime type of the loaded <see cref="RtStreamDataQuery"/>.</item>
///   <item><c>Aggregations</c> — computes statistical aggregates via a second engine call
///     using the same filter set.</item>
/// </list>
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class StreamDataQueryDtoType : ObjectGraphType<StreamDataQueryDto>
{
    private readonly ILogger<StreamDataQueryDtoType> _logger;

    /// <summary>Constructor.</summary>
    public StreamDataQueryDtoType(ILogger<StreamDataQueryDtoType> logger)
    {
        _logger = logger;
        Name = "StreamDataQuery";
        Description = "Descriptor for a persisted stream-data query. " +
                      "Use the Rows sub-connection to execute the query and the " +
                      "Aggregations sub-connection to compute statistics over the same data.";

        Field(d => d.QueryRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.AssociatedCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.Columns, typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<StreamDataQueryRowDtoType>>("Rows")
            .Description("Executes the persisted stream-data query and returns the result rows. " +
                         "Accepts optional runtime overrides for the time range, limit, and sort order, " +
                         "plus additional field filters AND-combined with the persisted FieldFilter.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Override time filter and limit at execution time.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg,
                "Sort order for items (overrides persisted sort if supplied).")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Additional field filters applied at execution time, AND-combined with the persisted FieldFilter.")
            .ResolveAsync(ResolveRowsAsync);

        Connection<NonNullGraphType<QueryAggregationResultType>>("Aggregations")
            .Description("Computes statistical aggregations over the same data set as the persisted query. " +
                         "Accepts optional runtime field filters AND-combined with the persisted FieldFilter.")
            .Argument<NonNullGraphType<ResultAggregationInputDtoType>>(Statics.AggregationsArg,
                "Requested aggregation statistics.")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Additional field filters applied at execution time, AND-combined with the persisted FieldFilter.")
            .ResolveAsync(ResolveAggregationsAsync);
    }

    // ─── Rows resolver ───────────────────────────────────────────────────────

    private async Task<object?> ResolveRowsAsync(IResolveConnectionContext<StreamDataQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataQueryDtoType: executing Rows sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataQueryUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var tenantId = gql.TenantId;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var loaded = uc.LoadedQuery;
            var ckTypeId = dto.AssociatedCkTypeId;

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(uc.ArchiveRtId)
                ?? throw new ArchiveNotFoundException(uc.ArchiveRtId);
            // Persisted queries dispatch by subtype; aggregation variants reference logical CK
            // paths on rollups (translated by the chain-aware engine resolver). The aggregation
            // builder is a no-op for raw / time-range archives and harmless for persisted Simple
            // queries that only reference physical paths, so we always use it here.
            var fieldResolver = await StreamDataFieldResolverExtensions.BuildAggregationFieldResolverAsync(
                archiveSnapshot, gql, ctx.CancellationToken);
            var execOverride = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

            // Runtime field filters AND-combine with the persisted FieldFilter on each subtype.
            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? runtimeFieldFilterDtos);
            var runtimeFieldFilters = runtimeFieldFilterDtos?.ToList();
            var mappedRuntimeFieldFilters = StreamDataGraphQlMapper.MapFieldFilters(runtimeFieldFilters);

            StreamQueryExecutionInput input;
            IReadOnlyList<ColumnNameMapping> resolvedColumnNames;

            switch (loaded)
            {
                case RtSimpleSdQuery simple:
                {
                    var columnNames = simple.Columns?.ToList() ?? [];
                    var fieldFilterList = simple.FieldFilter?.ToList();

                    // Downsampling override (AB#4233): a Simple query executed with
                    // queryMode=DOWNSAMPLING and a full from/to/limit contract reduces to `limit`
                    // bins per series instead of returning raw rows. The persisted type stays
                    // SimpleSdQuery — only the execution path changes. Without all three of
                    // from/to/limit we fall through to the raw simple path (raw fallback).
                    var dsFrom = execOverride?.From ?? simple.From;
                    var dsTo = execOverride?.To ?? simple.To;
                    var dsLimit = execOverride?.Limit
                        ?? (simple.Limit.HasValue ? (int)simple.Limit.Value : (int?)null);
                    if (execOverride?.QueryMode == QueryModeDto.Downsampling
                        && dsFrom is not null && dsTo is not null && dsLimit is not null)
                    {
                        var dsPersistedFilterPaths = fieldFilterList is { Count: > 0 }
                            ? fieldFilterList.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath)
                            : null;
                        var dsRuntimeFilterPaths = runtimeFieldFilters
                            ?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath);
                        StreamDataFieldValidation.ValidateStreamDataFields(
                            fieldResolver, columnNames, null,
                            ConcatNullable(dsPersistedFilterPaths, dsRuntimeFilterPaths));

                        // Per value-type reducers: numeric → AVG + MIN + MAX (envelope keeps peaks);
                        // string/enum/bool → MAX (a stable representative, exact for the
                        // constant-per-series columns like obisCode); other shapes are skipped.
                        var reducers = SynthesizeDownsamplingReducers(columnNames, dto.Columns);

                        // The downsampling engine path always surfaces the bin time under the
                        // canonical `timestamp` key (StreamDataRow.Timestamp + Values[timestamp]).
                        // Build that mapping directly rather than via ResolveToMappings, which would
                        // throw on windowed-storage archives whose resolver has no `timestamp`
                        // default key (their time axis is window_end).
                        resolvedColumnNames = new List<ColumnNameMapping>
                            {
                                new(Constants.Timestamp, Constants.Timestamp)
                            }
                            .Concat(fieldResolver.ResolveAggregationMappings(reducers))
                            .ToList();

                        input = new StreamQueryExecutionInput
                        {
                            Variant = StreamQueryVariant.Downsampling,
                            ArchiveRtId = uc.ArchiveRtId,
                            CkTypeId = ckTypeId,
                            AggregationColumns = reducers,
                            // Group each bin by the source rtId so interleaved series stay separated.
                            GroupByColumnPaths = new[] { Constants.RtId },
                            RtIds = execOverride?.RtIds ?? simple.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                            From = dsFrom,
                            To = dsTo,
                            Limit = dsLimit,
                            FieldFilters = MergeFilters(
                                StreamDataGraphQlMapper.MapCkFieldFilters(
                                    fieldFilterList,
                                    f => f.AttributePath,
                                    f => f.Operator,
                                    f => f.ComparisonValue),
                                mappedRuntimeFieldFilters)
                        };
                        break;
                    }

                    ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? runtimeSortDtos);
                    var runtimeSortList = runtimeSortDtos?.ToList();
                    var persistedSortList = simple.Sorting?.ToList();

                    var sortFieldNames = runtimeSortList is { Count: > 0 }
                        ? runtimeSortList.Select(s => s.AttributePath)
                        : persistedSortList?.Select(s => s.AttributePath);

                    var persistedFilterPaths = fieldFilterList is { Count: > 0 }
                        ? fieldFilterList.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath)
                        : null;
                    var runtimeFilterPaths = runtimeFieldFilters
                        ?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath);
                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver, columnNames, sortFieldNames,
                        ConcatNullable(persistedFilterPaths, runtimeFilterPaths));

                    resolvedColumnNames = fieldResolver.ResolveToMappings(columnNames);

                    IReadOnlyList<SortOrderItem>? sortOrders;
                    if (runtimeSortList is { Count: > 0 })
                    {
                        sortOrders = StreamDataGraphQlMapper.MapSortOrders(runtimeSortList);
                    }
                    else
                    {
                        sortOrders = StreamDataGraphQlMapper.MapCkSortOrders(
                            persistedSortList,
                            s => s.AttributePath,
                            s => s.SortOrder);
                    }

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Simple,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        ColumnPaths = columnNames,
                        RtIds = execOverride?.RtIds ?? simple.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? simple.From,
                        To = execOverride?.To ?? simple.To,
                        Limit = execOverride?.Limit ?? (simple.Limit.HasValue ? (int)simple.Limit.Value : null),
                        SortOrders = sortOrders,
                        FieldFilters = MergeFilters(
                            StreamDataGraphQlMapper.MapCkFieldFilters(
                                fieldFilterList,
                                f => f.AttributePath,
                                f => f.Operator,
                                f => f.ComparisonValue),
                            mappedRuntimeFieldFilters),
                        Offset = ctx.GetOffset(),
                        PageSize = ctx.First
                    };
                    break;
                }

                case RtAggregationSdQuery aggregation:
                {
                    var aggregationColumns = aggregation.Columns?.ToList() ?? [];
                    var fieldFilterList = aggregation.FieldFilter?.ToList();

                    var persistedFilterPaths = fieldFilterList is { Count: > 0 }
                        ? fieldFilterList.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath)
                        : null;
                    var runtimeFilterPaths = runtimeFieldFilters
                        ?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath);
                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        aggregationColumns.Select(c => c.AttributePath),
                        null,
                        ConcatNullable(persistedFilterPaths, runtimeFilterPaths));

                    var aggInputAgg = aggregationColumns
                        .Select(c => new AggregationColumn(
                            c.AttributePath,
                            StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                        .ToList();
                    resolvedColumnNames = fieldResolver.ResolveAggregationMappings(aggInputAgg);

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Aggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = execOverride?.RtIds ?? aggregation.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? aggregation.From,
                        To = execOverride?.To ?? aggregation.To,
                        FieldFilters = MergeFilters(
                            StreamDataGraphQlMapper.MapCkFieldFilters(
                                fieldFilterList,
                                f => f.AttributePath,
                                f => f.Operator,
                                f => f.ComparisonValue),
                            mappedRuntimeFieldFilters)
                    };
                    break;
                }

                case RtGroupingAggregationSdQuery grouping:
                {
                    var groupingColumns = grouping.GroupingColumns?.ToList() ?? [];
                    var aggregationColumns = grouping.Columns?.ToList() ?? [];
                    var fieldFilterList = grouping.FieldFilter?.ToList();

                    var persistedFilterPaths = fieldFilterList is { Count: > 0 }
                        ? fieldFilterList.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath)
                        : null;
                    var runtimeFilterPaths = runtimeFieldFilters
                        ?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath);
                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        groupingColumns.Concat(aggregationColumns.Select(c => c.AttributePath)),
                        null,
                        ConcatNullable(persistedFilterPaths, runtimeFilterPaths));

                    var aggInputGrp = aggregationColumns
                        .Select(c => new AggregationColumn(
                            c.AttributePath,
                            StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                        .ToList();
                    resolvedColumnNames = fieldResolver
                        .ResolveToMappings(groupingColumns)
                        .Concat(fieldResolver.ResolveAggregationMappings(aggInputGrp))
                        .ToList();

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.GroupingAggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        GroupByColumnPaths = groupingColumns,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = execOverride?.RtIds ?? grouping.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? grouping.From,
                        To = execOverride?.To ?? grouping.To,
                        FieldFilters = MergeFilters(
                            StreamDataGraphQlMapper.MapCkFieldFilters(
                                fieldFilterList,
                                f => f.AttributePath,
                                f => f.Operator,
                                f => f.ComparisonValue),
                            mappedRuntimeFieldFilters)
                    };
                    break;
                }

                case RtDownsamplingSdQuery downsampling:
                {
                    var aggregationColumns = downsampling.Columns?.ToList() ?? [];
                    var fieldFilterList = downsampling.FieldFilter?.ToList();

                    var persistedFilterPaths = fieldFilterList is { Count: > 0 }
                        ? fieldFilterList.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath)
                        : null;
                    var runtimeFilterPaths = runtimeFieldFilters
                        ?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath);
                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        aggregationColumns.Select(c => c.AttributePath),
                        null,
                        ConcatNullable(persistedFilterPaths, runtimeFilterPaths));

                    var aggInputDs = aggregationColumns
                        .Select(c => new AggregationColumn(
                            c.AttributePath,
                            StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                        .ToList();
                    // Timestamp first (canonical PascalCase, wire camelCase), then aggregation
                    // columns with function-suffixed keys (matches engine MapAggregationRow).
                    resolvedColumnNames = fieldResolver
                        .ResolveToMappings(new[] { Constants.Timestamp })
                        .Concat(fieldResolver.ResolveAggregationMappings(aggInputDs))
                        .ToList();

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Downsampling,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = execOverride?.RtIds ?? downsampling.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? downsampling.From,
                        To = execOverride?.To ?? downsampling.To,
                        Limit = execOverride?.Limit ?? (downsampling.Limit.HasValue
                            ? (int)downsampling.Limit.Value
                            : null),
                        FieldFilters = MergeFilters(
                            StreamDataGraphQlMapper.MapCkFieldFilters(
                                fieldFilterList,
                                f => f.AttributePath,
                                f => f.Operator,
                                f => f.ComparisonValue),
                            mappedRuntimeFieldFilters)
                    };
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unsupported RtStreamDataQuery subtype: {loaded.GetType().Name}");
            }

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug("StreamDataQueryDtoType Rows: got {Count} rows, totalCount={Total}",
                result.Rows.Count, result.TotalCount);

            var rows = result.Rows
                .Select(r => StreamDataQueryRowDto.FromStreamDataRow(r, resolvedColumnNames))
                .ToList();

            var effectiveOffset = input.Offset.GetValueOrDefault(0);
            return ConnectionUtils.ToOctoConnection(rows, ctx,
                rows.Count != 0 ? effectiveOffset : 0, (int)result.TotalCount);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    /// <summary>
    /// Builds the per-column reducer set for a Simple query executed in DOWNSAMPLING mode
    /// (AB#4233). The reducer is chosen from each column's value type: numeric columns get
    /// AVG + MIN + MAX (the MIN/MAX envelope preserves peaks the AVG centre line would smooth
    /// away); string / enum / boolean / temporal columns get MAX as a stable representative
    /// (exact for series-identifying columns that are constant within a (bin, series) group, e.g.
    /// obisCode); record / array / binary / geospatial shapes are not chartable scalars and are
    /// skipped. The bin timestamp is supplied separately as the "T" column.
    /// </summary>
    private static List<AggregationColumn> SynthesizeDownsamplingReducers(
        IReadOnlyList<string> columnPaths,
        IReadOnlyList<RtQueryColumnDto> columns)
    {
        var typeByPath = new Dictionary<string, AttributeValueTypesDto>();
        foreach (var c in columns)
        {
            typeByPath[c.AttributePath] = c.AttributeValueType;
        }

        var reducers = new List<AggregationColumn>();
        foreach (var path in columnPaths)
        {
            if (!typeByPath.TryGetValue(path, out var valueType))
            {
                continue;
            }

            switch (valueType)
            {
                case AttributeValueTypesDto.Integer:
                case AttributeValueTypesDto.Integer64:
                case AttributeValueTypesDto.Double:
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Average));
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Minimum));
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Maximum));
                    break;

                case AttributeValueTypesDto.String:
                case AttributeValueTypesDto.Enum:
                case AttributeValueTypesDto.Boolean:
                case AttributeValueTypesDto.DateTime:
                case AttributeValueTypesDto.DateTimeOffset:
                case AttributeValueTypesDto.TimeSpan:
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Maximum));
                    break;

                default:
                    // Records, arrays, binaries, geospatial — not reducible to a chartable scalar.
                    break;
            }
        }

        return reducers;
    }

    // ─── Aggregations resolver ────────────────────────────────────────────────

    private async Task<object?> ResolveAggregationsAsync(
        IResolveConnectionContext<StreamDataQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataQueryDtoType: executing Aggregations sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataQueryUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var loaded = uc.LoadedQuery;
            var ckTypeId = dto.AssociatedCkTypeId;
            var aggInput = ctx.GetArgument<ResultAggregationInputDto>(Statics.AggregationsArg);

            // Build aggregation columns from the requested stats input.
            // Each (stat × path) pair becomes one AggregationColumn.
            var aggColumns = new List<AggregationColumn>();
            if (aggInput.CountAttributePaths != null)
                aggColumns.AddRange(aggInput.CountAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Count)));
            if (aggInput.MinValueAttributePaths != null)
                aggColumns.AddRange(aggInput.MinValueAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Minimum)));
            if (aggInput.MaxValueAttributePaths != null)
                aggColumns.AddRange(aggInput.MaxValueAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Maximum)));
            if (aggInput.AvgAttributePaths != null)
                aggColumns.AddRange(aggInput.AvgAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Average)));
            if (aggInput.SumAttributePaths != null)
                aggColumns.AddRange(aggInput.SumAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Sum)));

            // Re-use the same field filters as the loaded query (same data-set semantics),
            // AND-combined with any runtime field filters passed alongside the aggregations request.
            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? runtimeFieldFilterDtos);
            var fieldFilters = MergeFilters(
                StreamDataGraphQlMapper.MapCkFieldFilters(
                    loaded.FieldFilter?.ToList(),
                    f => f.AttributePath,
                    f => f.Operator,
                    f => f.ComparisonValue),
                StreamDataGraphQlMapper.MapFieldFilters(runtimeFieldFilterDtos?.ToList()));

            var input = new StreamQueryExecutionInput
            {
                Variant = StreamQueryVariant.Aggregation,
                ArchiveRtId = uc.ArchiveRtId,
                CkTypeId = ckTypeId,
                AggregationColumns = aggColumns,
                RtIds = loaded.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                From = loaded.From,
                To = loaded.To,
                FieldFilters = fieldFilters
            };

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug("StreamDataQueryDtoType Aggregations: got {Count} agg rows",
                result.Rows.Count);

            // Project the single aggregation row into QueryAggregationResult.
            // The engine returns stat values keyed by "Count_Path", "Min_Path", etc.
            var countStats = StreamDataStatisticsHelper.BuildStats(aggInput.CountAttributePaths, result, "Count");
            var minStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MinValueAttributePaths, result, "Min");
            var maxStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MaxValueAttributePaths, result, "Max");
            var avgStats   = StreamDataStatisticsHelper.BuildStats(aggInput.AvgAttributePaths, result, "Avg");
            var sumStats   = StreamDataStatisticsHelper.BuildStats(aggInput.SumAttributePaths, result, "Sum");

            // TotalCount from the first aggregation row, or 0 if no rows
            var totalCount = result.TotalCount;

            var aggregationResult = new QueryAggregationResult(
                totalCount,
                countStats,
                minStats,
                maxStats,
                avgStats,
                sumStats,
                null);

            return ConnectionUtils.ToOctoConnection(
                new[] { aggregationResult }, ctx, 0, 1);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// AND-combines persisted and runtime field filters. Either input may be null/empty.
    /// Returns null when both are null/empty (engine-native "no filter").
    /// </summary>
    private static IReadOnlyList<FieldFilter>? MergeFilters(
        IReadOnlyList<FieldFilter>? persisted,
        IReadOnlyList<FieldFilter>? runtime)
    {
        if ((persisted == null || persisted.Count == 0) && (runtime == null || runtime.Count == 0))
            return null;
        if (persisted == null || persisted.Count == 0) return runtime;
        if (runtime == null || runtime.Count == 0) return persisted;
        return persisted.Concat(runtime).ToList();
    }

    private static IEnumerable<string>? ConcatNullable(IEnumerable<string>? a, IEnumerable<string>? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a.Concat(b);
    }
}
