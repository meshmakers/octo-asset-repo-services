using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

// ReSharper disable once UnusedType.Global
/// <summary>
/// GraphQL namespace type for transient (ad-hoc) stream-data queries.
/// Hosts four sub-connections — Simple, Aggregation, GroupingAggregation, Downsampling —
/// each returning a <see cref="StreamDataTransientQueryDto"/> descriptor with
/// <c>.Rows</c> and <c>.Aggregations</c> sub-connections.
/// </summary>
internal sealed class StreamDataTransientQuery : ObjectGraphType
{
    private readonly ILogger<StreamDataTransientQuery> _logger;

    /// <inheritdoc />
    public StreamDataTransientQuery(ILogger<StreamDataTransientQuery> logger)
    {
        _logger = logger;
        Name = "StreamDataTransient";
        Description = "Transient stream-data queries constructed ad-hoc at execution time.";

        Connection<NonNullGraphType<StreamDataTransientQueryDtoType>>("Simple")
            .Description("Transient simple stream-data query — projects raw attribute values.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg,
                "CkArchive runtime id whose table should be queried.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.ColumnPathsArg,
                "Data stream attribute names to project.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Time filter and limit.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg,
                "Sort order for items.")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Field-level comparison filters.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Scope to specific runtime entity IDs.")
            .ResolveAsync(ResolveSimpleAsync);

        Connection<NonNullGraphType<StreamDataTransientQueryDtoType>>("Aggregation")
            .Description("Transient aggregation stream-data query.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg,
                "CkArchive runtime id whose table should be queried.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StreamDataQueryColumnInputDtoType>>>>(Statics.ColumnPathsArg,
                "Aggregation columns with attribute path and aggregation type.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Time filter and limit.")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Field-level comparison filters.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Scope to specific runtime entity IDs.")
            .ResolveAsync(ResolveAggregationAsync);

        Connection<NonNullGraphType<StreamDataTransientQueryDtoType>>("GroupingAggregation")
            .Description("Transient grouped-aggregation stream-data query.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg,
                "CkArchive runtime id whose table should be queried.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.GroupByColumnPathsArg,
                "The attribute paths to group by.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StreamDataQueryColumnInputDtoType>>>>(Statics.ColumnPathsArg,
                "Aggregation columns with attribute path and aggregation type.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Time filter and limit.")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Field-level comparison filters.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Scope to specific runtime entity IDs.")
            .ResolveAsync(ResolveGroupingAggregationAsync);

        Connection<NonNullGraphType<StreamDataTransientQueryDtoType>>("Downsampling")
            .Description("Transient downsampling stream-data query — divides the time range into equal buckets.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg,
                "CkArchive runtime id whose table should be queried.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StreamDataQueryColumnInputDtoType>>>>(Statics.ColumnPathsArg,
                "Aggregation columns with attribute path and aggregation type.")
            .Argument<NonNullGraphType<IntGraphType>>("limit",
                "Number of time buckets to produce.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("from",
                "Start of time range.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("to",
                "End of time range.")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Field-level comparison filters.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Scope to specific runtime entity IDs.")
            .ResolveAsync(ResolveDownsamplingAsync);
    }

    // ─── Simple ───────────────────────────────────────────────────────────────

    private async Task<object?> ResolveSimpleAsync(IResolveConnectionContext<object?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataTransientQuery: handling Simple sub-connection");

            var gql = (GraphQlUserContext)ctx.UserContext;
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);

            var columnPaths = ctx.GetArgument<IEnumerable<string>>(Statics.ColumnPathsArg).ToList();

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            var ckTypeId = archiveSnapshot.TargetCkTypeId;
            var fieldResolver = BuildFieldResolver(archiveSnapshot);

            ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? sortDtos);
            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtos);
            ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var execArgs = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);
            var fieldFilters = fieldFilterDtos?.ToList();

            StreamDataFieldValidation.ValidateStreamDataFields(
                fieldResolver, columnPaths,
                sortDtos?.Select(s => s.AttributePath),
                fieldFilters?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath));

            var typeQueryColumns = ctx.GetCkCacheService()
                .GetCkTypeQueryColumnPathsByRtCkId(gql.TenantId, ckTypeId);
            var columns = columnPaths
                .Select(p => BuildSimpleColumn(p, typeQueryColumns))
                .ToList();

            var dto = new StreamDataTransientQueryDto
            {
                QueryCkTypeId = ckTypeId,
                Columns = columns,
                UserContext = new StreamDataTransientUserContext
                {
                    Variant = StreamQueryVariant.Simple,
                    ArchiveRtId = archiveRtId,
                    CkTypeId = ckTypeId,
                    ColumnPaths = columnPaths,
                    From = execArgs?.From,
                    To = execArgs?.To,
                    Limit = execArgs?.Limit,
                    SortOrders = StreamDataGraphQlMapper.MapSortOrders(sortDtos),
                    FieldFilters = StreamDataGraphQlMapper.MapFieldFilters(fieldFilters),
                    RtIds = rtIds?.ToList()
                }
            };

            return ConnectionUtils.ToOctoConnection(new[] { dto }, ctx, 0, 1);
        }
        catch (Exception e) { return ctx.HandleException(e); }
    }

    // ─── Aggregation ──────────────────────────────────────────────────────────

    private async Task<object?> ResolveAggregationAsync(IResolveConnectionContext<object?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataTransientQuery: handling Aggregation sub-connection");

            var gql = (GraphQlUserContext)ctx.UserContext;
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);

            var columnInputs = ctx.GetArgument<IEnumerable<StreamDataQueryColumnInputDto>>(Statics.ColumnPathsArg).ToList();

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            var ckTypeId = archiveSnapshot.TargetCkTypeId;
            var fieldResolver = await StreamDataFieldResolverExtensions.BuildAggregationFieldResolverAsync(archiveSnapshot, gql, ctx.CancellationToken);

            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtos);
            ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var execArgs = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);
            var fieldFilters = fieldFilterDtos?.ToList();

            StreamDataFieldValidation.ValidateStreamDataFields(
                fieldResolver,
                columnInputs.Select(c => c.AttributePath),
                null,
                fieldFilters?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath));

            var aggColumns = columnInputs
                .Select(c => new AggregationColumn(
                    c.AttributePath,
                    StreamDataGraphQlMapper.MapAggregationFunctionDto(c.AggregationType)))
                .ToList();

            var typeQueryColumns = ctx.GetCkCacheService()
                .GetCkTypeQueryColumnPathsByRtCkId(gql.TenantId, ckTypeId);
            var columns = columnInputs
                .Select(c => BuildAggregationColumn(c.AttributePath, MapAggregationFunctionDtoToDto(c.AggregationType), typeQueryColumns))
                .ToList();

            var dto = new StreamDataTransientQueryDto
            {
                QueryCkTypeId = ckTypeId,
                Columns = columns,
                UserContext = new StreamDataTransientUserContext
                {
                    Variant = StreamQueryVariant.Aggregation,
                    ArchiveRtId = archiveRtId,
                    CkTypeId = ckTypeId,
                    AggregationColumns = aggColumns,
                    From = execArgs?.From,
                    To = execArgs?.To,
                    FieldFilters = StreamDataGraphQlMapper.MapFieldFilters(fieldFilters),
                    RtIds = rtIds?.ToList()
                }
            };

            return ConnectionUtils.ToOctoConnection(new[] { dto }, ctx, 0, 1);
        }
        catch (Exception e) { return ctx.HandleException(e); }
    }

    // ─── GroupingAggregation ──────────────────────────────────────────────────

    private async Task<object?> ResolveGroupingAggregationAsync(IResolveConnectionContext<object?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataTransientQuery: handling GroupingAggregation sub-connection");

            var gql = (GraphQlUserContext)ctx.UserContext;
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);

            var groupByColumnPaths = ctx.GetArgument<IEnumerable<string>>(Statics.GroupByColumnPathsArg).ToList();
            var columnInputs = ctx.GetArgument<IEnumerable<StreamDataQueryColumnInputDto>>(Statics.ColumnPathsArg).ToList();

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            var ckTypeId = archiveSnapshot.TargetCkTypeId;
            var fieldResolver = await StreamDataFieldResolverExtensions.BuildAggregationFieldResolverAsync(archiveSnapshot, gql, ctx.CancellationToken);

            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtos);
            ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var execArgs = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);
            var fieldFilters = fieldFilterDtos?.ToList();

            StreamDataFieldValidation.ValidateStreamDataFields(
                fieldResolver,
                groupByColumnPaths.Concat(columnInputs.Select(c => c.AttributePath)),
                null,
                fieldFilters?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath));

            var aggColumns = columnInputs
                .Select(c => new AggregationColumn(
                    c.AttributePath,
                    StreamDataGraphQlMapper.MapAggregationFunctionDto(c.AggregationType)))
                .ToList();

            var typeQueryColumns = ctx.GetCkCacheService()
                .GetCkTypeQueryColumnPathsByRtCkId(gql.TenantId, ckTypeId);
            var groupByColumns = groupByColumnPaths
                .Select(p => BuildSimpleColumn(p, typeQueryColumns));
            var aggColumnDtos = columnInputs
                .Select(c => BuildAggregationColumn(c.AttributePath, MapAggregationFunctionDtoToDto(c.AggregationType), typeQueryColumns));
            var columns = groupByColumns.Concat(aggColumnDtos).ToList();

            var dto = new StreamDataTransientQueryDto
            {
                QueryCkTypeId = ckTypeId,
                Columns = columns,
                UserContext = new StreamDataTransientUserContext
                {
                    Variant = StreamQueryVariant.GroupingAggregation,
                    ArchiveRtId = archiveRtId,
                    CkTypeId = ckTypeId,
                    GroupByColumnPaths = groupByColumnPaths,
                    AggregationColumns = aggColumns,
                    From = execArgs?.From,
                    To = execArgs?.To,
                    FieldFilters = StreamDataGraphQlMapper.MapFieldFilters(fieldFilters),
                    RtIds = rtIds?.ToList()
                }
            };

            return ConnectionUtils.ToOctoConnection(new[] { dto }, ctx, 0, 1);
        }
        catch (Exception e) { return ctx.HandleException(e); }
    }

    // ─── Downsampling ─────────────────────────────────────────────────────────

    private async Task<object?> ResolveDownsamplingAsync(IResolveConnectionContext<object?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataTransientQuery: handling Downsampling sub-connection");

            var gql = (GraphQlUserContext)ctx.UserContext;
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);

            var columnInputs = ctx.GetArgument<IEnumerable<StreamDataQueryColumnInputDto>>(Statics.ColumnPathsArg).ToList();
            var from = ctx.GetArgument<DateTime>("from");
            var to = ctx.GetArgument<DateTime>("to");
            var limit = ctx.GetArgument<int>("limit");

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            var ckTypeId = archiveSnapshot.TargetCkTypeId;
            var fieldResolver = await StreamDataFieldResolverExtensions.BuildAggregationFieldResolverAsync(archiveSnapshot, gql, ctx.CancellationToken);

            ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtos);
            ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var fieldFilters = fieldFilterDtos?.ToList();

            StreamDataFieldValidation.ValidateStreamDataFields(
                fieldResolver,
                columnInputs.Select(c => c.AttributePath),
                null,
                fieldFilters?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath));

            var aggColumns = columnInputs
                .Select(c => new AggregationColumn(
                    c.AttributePath,
                    StreamDataGraphQlMapper.MapAggregationFunctionDto(c.AggregationType)))
                .ToList();

            var typeQueryColumns = ctx.GetCkCacheService()
                .GetCkTypeQueryColumnPathsByRtCkId(gql.TenantId, ckTypeId);
            var columns = columnInputs
                .Select(c => BuildAggregationColumn(c.AttributePath, MapAggregationFunctionDtoToDto(c.AggregationType), typeQueryColumns))
                .ToList();

            var dto = new StreamDataTransientQueryDto
            {
                QueryCkTypeId = ckTypeId,
                Columns = columns,
                UserContext = new StreamDataTransientUserContext
                {
                    Variant = StreamQueryVariant.Downsampling,
                    ArchiveRtId = archiveRtId,
                    CkTypeId = ckTypeId,
                    AggregationColumns = aggColumns,
                    From = from,
                    To = to,
                    Limit = limit,
                    FieldFilters = StreamDataGraphQlMapper.MapFieldFilters(fieldFilters),
                    RtIds = rtIds?.ToList()
                }
            };

            return ConnectionUtils.ToOctoConnection(new[] { dto }, ctx, 0, 1);
        }
        catch (Exception e) { return ctx.HandleException(e); }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the field resolver for a transient stream-data query. Attribute paths come from the
    /// CkArchive's column spec — that's the canonical set of paths the per-archive table actually
    /// stores after T17.
    /// </summary>
    private static StreamDataFieldResolver BuildFieldResolver(ArchiveSnapshot archiveSnapshot)
    {
        return new StreamDataFieldResolver(
            archiveSnapshot.Columns.Select(c => c.Path),
            usesWindowedStorage: archiveSnapshot.UsesWindowedStorage);
    }

    private static AggregationTypesDto MapAggregationFunctionDtoToDto(AggregationFunctionDto func)
    {
        return func switch
        {
            AggregationFunctionDto.Avg   => AggregationTypesDto.Average,
            AggregationFunctionDto.Min   => AggregationTypesDto.Minimum,
            AggregationFunctionDto.Max   => AggregationTypesDto.Maximum,
            AggregationFunctionDto.Count => AggregationTypesDto.Count,
            AggregationFunctionDto.Sum   => AggregationTypesDto.Sum,
            _ => AggregationTypesDto.None
        };
    }

    /// <summary>
    /// Builds a simple (non-aggregating) column DTO. Resolves the value type
    /// from the CK model when the path is known; falls back to the String
    /// value type for paths the resolver doesn't recognise (e.g. synthesized
    /// downsampling output columns like window_start / window_end).
    /// </summary>
    private static RtQueryColumnDto BuildSimpleColumn(
        string attributePath,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns)
    {
        var ck = typeQueryColumns.FirstOrDefault(c => c.Path == attributePath);
        return new RtQueryColumnDto
        {
            AttributePath = attributePath,
            AttributeValueType = ck?.ValueType
                ?? Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
            AggregationType = AggregationTypesDto.None
        };
    }

    /// <summary>
    /// Builds an aggregation column DTO with the result type adjusted per
    /// aggregation function (Count -> Integer, Average -> Double; min/max/sum
    /// preserve the source type). Mirrors the runtime resolver's behavior
    /// in <c>RtQueryColumnType.GetAggregationResultType</c>.
    /// </summary>
    private static RtQueryColumnDto BuildAggregationColumn(
        string attributePath,
        AggregationTypesDto aggregationType,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns)
    {
        var ck = typeQueryColumns.FirstOrDefault(c => c.Path == attributePath);
        var sourceType = ck?.ValueType
            ?? Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String;
        return new RtQueryColumnDto
        {
            AttributePath = attributePath,
            AttributeValueType = GetAggregationResultType(sourceType, aggregationType),
            AggregationType = aggregationType
        };
    }

    private static Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto GetAggregationResultType(
        Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto sourceType,
        AggregationTypesDto aggregationType)
    {
        return aggregationType switch
        {
            AggregationTypesDto.None => sourceType,
            AggregationTypesDto.Count => Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Integer,
            AggregationTypesDto.Sum => sourceType,
            AggregationTypesDto.Average => Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Double,
            AggregationTypesDto.Minimum => sourceType,
            AggregationTypesDto.Maximum => sourceType,
            _ => sourceType
        };
    }
}
