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
                rollup.Aggregations.Count));
        }
        return result;
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
            var columns = BuildColumnsFromLoaded(loaded);

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

    private static IReadOnlyList<RtQueryColumnDto> BuildColumnsFromLoaded(RtStreamDataQuery loaded)
    {
        switch (loaded)
        {
            case RtSimpleSdQuery simple:
                return (simple.Columns?.ToList() ?? [])
                    .Select(path => new RtQueryColumnDto
                    {
                        AttributePath = path,
                        AttributeValueType = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
                        AggregationType = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.None
                    })
                    .ToList();

            case RtAggregationSdQuery aggregation:
                return (aggregation.Columns?.ToList() ?? [])
                    .Select(c => new RtQueryColumnDto
                    {
                        AttributePath = c.AttributePath,
                        AttributeValueType = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
                        AggregationType = MapCkAggregationTypeToDto(c.AggregationType)
                    })
                    .ToList();

            case RtGroupingAggregationSdQuery grouping:
                var groupingCols = (grouping.GroupingColumns?.ToList() ?? [])
                    .Select(path => new RtQueryColumnDto
                    {
                        AttributePath = path,
                        AttributeValueType = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
                        AggregationType = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AggregationTypesDto.None
                    });
                var aggCols = (grouping.Columns?.ToList() ?? [])
                    .Select(c => new RtQueryColumnDto
                    {
                        AttributePath = c.AttributePath,
                        AttributeValueType = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
                        AggregationType = MapCkAggregationTypeToDto(c.AggregationType)
                    });
                return groupingCols.Concat(aggCols).ToList();

            case RtDownsamplingSdQuery downsampling:
                return (downsampling.Columns?.ToList() ?? [])
                    .Select(c => new RtQueryColumnDto
                    {
                        AttributePath = c.AttributePath,
                        AttributeValueType = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
                        AggregationType = MapCkAggregationTypeToDto(c.AggregationType)
                    })
                    .ToList();

            default:
                return [];
        }
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
