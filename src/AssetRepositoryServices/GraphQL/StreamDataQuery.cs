using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class StreamDataQuery : ObjectGraphType
{
    private readonly ILogger<StreamDataQuery> _logger;

    public StreamDataQuery(ILogger<StreamDataQuery> logger)
    {
        _logger = logger;
        Name = "StreamDataModelQuery";

        Connection<NonNullGraphType<StreamDataQueryDtoType>>("StreamDataQuery")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The persisted stream-data query runtime id. The query's stored ArchiveRtId determines which archive table is read.")
            .ResolveAsync(ResolveStreamDataQueryAsync);

        Field<NonNullGraphType<StreamDataTransientQuery>>("TransientStreamDataQuery")
            .Description("Transient stream-data queries")
            .Resolve(_ => new { });

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RollupArchiveInfoDtoType>>>>("rollupsFor")
            .Description("Returns every non-soft-deleted rollup archive attached to the given source archive — runtime id, status, schedule, watermark, freeze state. Rollup-archives concept §9.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the source CkArchive to enumerate rollups for.")
            .ResolveAsync(ResolveRollupsForAsync);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RecomputeJobInfoDtoType>>>>("recomputeJobsFor")
            .Description("Returns the most recent recompute jobs for a rollup archive (newest first, capped at 50) — for debugging why a recompute failed. AB#4184.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to list recompute jobs for.")
            .ResolveAsync(ResolveRecomputeJobsForAsync);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<ArchiveStorageStatsDtoType>>>>("archivesStorageStats")
            .Description("Bulk-fetch per-archive backend storage stats (row count, on-disk size, health) for the studio's archives list. One round-trip per call; archives whose backing table doesn't exist yet (not activated) appear with tableExists=false so callers don't have to filter the rtId list beforehand.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<OctoObjectIdType>>>>("rtIds", "Runtime ids of the archives to fetch stats for. Empty list returns empty result.")
            .ResolveAsync(ResolveArchivesStorageStatsAsync);

        Field<RollupQueryMetadataDtoType>("rollupQueryMetadata")
            .Description("Returns the studio's query-editor metadata for a rollup archive: bucket size and the distinct *logical* CK-attribute paths the rollup aggregates. Cascade rollups (rollup over rollup) have their physical sourcePath storage columns reversed back to the original CK attribute paths via RollupLogicalPathResolver (concept-time-range §7). Null if the rtId doesn't resolve to a rollup archive.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the rollup archive to fetch metadata for.")
            .ResolveAsync(ResolveRollupQueryMetadataAsync);

        Field<ResolveSeriesQueryDtoType>("resolveSeriesQuery")
            .Description("Resolution-aware series routing (AB#4290): given a base archive family, a time window, a target point count and the required aggregation, returns the archive/rollup to query at the best resolution — without the caller knowing which physical archive holds the data at a usable grain. The caller then runs the existing downsampling query against the returned archiveRtId with limit = points. Null if StreamData is not enabled for the tenant.")
            .Argument<NonNullGraphType<ResolveSeriesQueryInputType>>("input", "The series identity, time window, target point count and required aggregation.")
            .ResolveAsync(ResolveSeriesQueryAsync);
    }

    private static async Task<object?> ResolveSeriesQueryAsync(IResolveFieldContext<object?> ctx)
    {
        var input = ctx.GetArgument<ResolveSeriesQueryInputDto>("input");
        var gql = (GraphQlUserContext)ctx.UserContext;
        var rollupStore = gql.TenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore is null)
        {
            // StreamData not enabled for this tenant — surface null so callers can fall back.
            return null;
        }

        var archiveStore = gql.TenantContext.GetArchiveRuntimeStore();
        var service = new SeriesResolutionService(archiveStore, new RollupDependencyGraph(rollupStore));

        var request = new SeriesResolutionRequest(
            input.BaseArchiveRtId,
            TargetCkTypeId: null,
            input.From,
            input.To,
            input.TargetPoints,
            input.RequiredAggregation,
            input.SourcePath)
        {
            RtIds = input.RtIds,
            ObisFilter = input.ObisFilter,
        };

        var result = await service.ResolveAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        return new ResolveSeriesQueryDto(
            result.ArchiveRtId,
            result.EffectiveBucketMs,
            result.Points,
            result.ReducingFunction,
            result.Signal,
            result.ActualPoints,
            result.Diagnostic);
    }

    private static async Task<object?> ResolveRollupQueryMetadataAsync(IResolveFieldContext<object?> ctx)
    {
        var rollupRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var gql = (GraphQlUserContext)ctx.UserContext;
        var rollupStore = gql.TenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore is null)
        {
            // StreamData not enabled for this tenant — surface null so the studio can render
            // without the bucket-alignment hint / logical-path picker.
            return null;
        }

        var rollup = await rollupStore.GetAsync(rollupRtId).ConfigureAwait(false);
        if (rollup is null)
        {
            return null;
        }

        var archiveStore = gql.TenantContext.GetArchiveRuntimeStore();
        var logicalPaths = await RollupLogicalPathResolver.ResolveAsync(
            rollup,
            getArchive: id => archiveStore.GetAsync(id),
            getRollup: id => rollupStore.GetAsync(id),
            ctx.CancellationToken).ConfigureAwait(false);

        return new RollupQueryMetadataDto(
            RtId: rollup.RtId,
            BucketSizeMs: (long)rollup.BucketSize.TotalMilliseconds,
            LogicalSourcePaths: logicalPaths);
    }

    private static async Task<object?> ResolveArchivesStorageStatsAsync(IResolveFieldContext<object?> ctx)
    {
        var rtIds = ctx.GetArgument<IReadOnlyList<OctoObjectId>>("rtIds") ?? Array.Empty<OctoObjectId>();
        if (rtIds.Count == 0)
        {
            return Array.Empty<ArchiveStorageStatsDto>();
        }

        var gql = (GraphQlUserContext)ctx.UserContext;
        var streamDataRepo = gql.TenantContext.GetStreamDataRepository();
        if (streamDataRepo is null)
        {
            // StreamData not enabled for this tenant — return placeholders so the studio's list
            // can still render the rows without a special "stats unavailable" code path.
            return rtIds
                .Select(rtId => new ArchiveStorageStatsDto(rtId, TableExists: false, RecordCount: 0, SizeBytes: 0, Health: ArchiveStorageHealth.Unknown))
                .ToList();
        }

        var stats = await streamDataRepo.GetArchiveStatsAsync(rtIds, ctx.CancellationToken).ConfigureAwait(false);

        // Preserve the input order so studio clients can zip with their existing row list.
        return rtIds
            .Select(rtId => stats.TryGetValue(rtId, out var s)
                ? new ArchiveStorageStatsDto(s.ArchiveRtId, s.TableExists, s.RecordCount, s.SizeBytes, s.Health)
                : new ArchiveStorageStatsDto(rtId, TableExists: false, RecordCount: 0, SizeBytes: 0, Health: ArchiveStorageHealth.Unknown))
            .ToList();
    }

    private static async Task<object?> ResolveRollupsForAsync(IResolveFieldContext<object?> ctx)
    {
        var sourceRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var gql = (GraphQlUserContext)ctx.UserContext;
        var rollupStore = gql.TenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore is null)
        {
            return Array.Empty<RollupArchiveInfoDto>();
        }

        var result = new List<RollupArchiveInfoDto>();
        await foreach (var rollup in rollupStore.EnumerateAsync())
        {
            if (rollup.SourceArchiveRtId != sourceRtId) continue;
            result.Add(new RollupArchiveInfoDto(
                rollup.RtId,
                rollup.RtWellKnownName,
                rollup.Status,
                rollup.SourceArchiveRtId,
                (long)rollup.BucketSize.TotalMilliseconds,
                (long)rollup.WatermarkLag.TotalMilliseconds,
                rollup.LastAggregatedBucketEnd,
                rollup.FrozenUntil,
                rollup.Aggregations.Count,
                rollup.RecomputeInProgress,
                rollup.LastRecomputeStartedAt,
                rollup.LastRecomputeSuccessAt,
                rollup.LastRecomputeFailureAt,
                rollup.LastRecomputeFailureReason,
                rollup.DirtyWindowsPending,
                rollup.PendingRecomputeRanges,
                rollup.BucketAlignment.ToString(),
                rollup.ReferenceTimeZone,
                rollup.Aggregations
                    .Select(a => new RollupAggregationInfoDto(a.SourcePath, a.Function.ToString()))
                    .ToList()));
        }
        return result;
    }

    private static async Task<object?> ResolveRecomputeJobsForAsync(IResolveFieldContext<object?> ctx)
    {
        var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var gql = (GraphQlUserContext)ctx.UserContext;
        var jobStore = gql.TenantContext.GetRecomputeJobStore();
        if (jobStore is null)
        {
            return Array.Empty<RecomputeJobInfoDto>();
        }

        var jobs = await jobStore.GetForArchiveAsync(archiveRtId, 50);
        return jobs.Select(RecomputeJobInfoDto.From).ToList();
    }

    private async Task<object?> ResolveStreamDataQueryAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for persisted stream data query descriptor started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            var queryRtId = arg.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var loaded = await tenantRepository.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                sessionAccessor.Session, queryRtId)
                ?? throw AssetRepositoryException.RtQueryNotFound(queryRtId);

            // The archive target is captured on the persisted query — the resolver doesn't
            // need a separate argument. Persisted query is a complete specification.
            if (string.IsNullOrWhiteSpace(loaded.ArchiveRtId))
            {
                throw AssetRepositoryException.RtQueryNotFound(queryRtId);
            }
            var archiveRtId = new OctoObjectId(loaded.ArchiveRtId);

            // Build the column list from the concrete subtype so clients can inspect them.
            // The CK cache lookup gives us the real attributeValueType per column path —
            // hardcoding `STRING` here (the bug) breaks numeric pickers in studio dialogs.
            var ckCacheService = arg.GetCkCacheService();
            var typeQueryColumns = ckCacheService
                .GetCkTypeQueryColumnPathsByRtCkId(graphQlUserContext.TenantId, new RtCkId<CkTypeId>(loaded.QueryCkTypeId));
            var columns = BuildColumnsFromLoaded(loaded, typeQueryColumns);

            var dto = new StreamDataQueryDto
            {
                QueryRtId = queryRtId,
                AssociatedCkTypeId = new RtCkId<CkTypeId>(loaded.QueryCkTypeId),
                Columns = columns,
                UserContext = new StreamDataQueryUserContext
                {
                    LoadedQuery = loaded,
                    ArchiveRtId = archiveRtId,
                }
            };

            return ConnectionUtils.ToOctoConnection(new[] { dto }, arg, 0, 1);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private static IReadOnlyList<RtQueryColumnDto> BuildColumnsFromLoaded(
        RtStreamDataQuery loaded,
        IReadOnlyCollection<CkTypeQueryColumn> typeQueryColumns)
    {
        switch (loaded)
        {
            case RtSimpleSdQuery simple:
                return (simple.Columns?.ToList() ?? [])
                    .Select(path => BuildSimpleColumn(path, typeQueryColumns))
                    .ToList();

            case RtAggregationSdQuery aggregation:
                return (aggregation.Columns?.ToList() ?? [])
                    .Select(c => BuildAggregationColumn(c.AttributePath, MapCkAggregationTypeToDto(c.AggregationType), typeQueryColumns))
                    .ToList();

            case RtGroupingAggregationSdQuery grouping:
                var groupingCols = (grouping.GroupingColumns?.ToList() ?? [])
                    .Select(path => BuildSimpleColumn(path, typeQueryColumns));
                var aggCols = (grouping.Columns?.ToList() ?? [])
                    .Select(c => BuildAggregationColumn(c.AttributePath, MapCkAggregationTypeToDto(c.AggregationType), typeQueryColumns));
                return groupingCols.Concat(aggCols).ToList();

            case RtDownsamplingSdQuery downsampling:
                return (downsampling.Columns?.ToList() ?? [])
                    .Select(c => BuildAggregationColumn(c.AttributePath, MapCkAggregationTypeToDto(c.AggregationType), typeQueryColumns))
                    .ToList();

            default:
                return [];
        }
    }

    /// <summary>
    /// Builds a simple (non-aggregating) column DTO. Resolves the value type from
    /// the CK model when the path is known; falls back to the String value type
    /// for synthesized columns the resolver wouldn't recognise (e.g. window_start /
    /// window_end emitted by downsampling/rollup, or aggregation result columns
    /// that don't exist as raw attributes on the target CK type).
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
            AggregationType = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.None
        };
    }

    /// <summary>
    /// Builds an aggregation column DTO with the result type adjusted per
    /// aggregation function (Count → Integer, Average → Double; min/max/sum
    /// preserve the source type). Mirrors the runtime resolver's behavior
    /// in <c>RtQueryColumnType.GetAggregationResultType</c>.
    /// </summary>
    private static RtQueryColumnDto BuildAggregationColumn(
        string attributePath,
        Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto aggregationType,
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
        Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto aggregationType)
    {
        return aggregationType switch
        {
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.None => sourceType,
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Count => Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Integer,
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Sum => sourceType,
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Average => Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Double,
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Minimum => sourceType,
            Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Maximum => sourceType,
            _ => sourceType
        };
    }

    private static Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto
        MapCkAggregationTypeToDto(Enum ckEnum)
    {
        return ckEnum.ToString() switch
        {
            "Count"   => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Count,
            "Sum"     => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Sum,
            "Average" => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Average,
            "Avg"     => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Average,
            "Minimum" => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Minimum,
            "Min"     => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Minimum,
            "Maximum" => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Maximum,
            "Max"     => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.Maximum,
            _ => Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.None
        };
    }

}
