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
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The persisted stream-data query runtime id.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg, "CkArchive runtime id the persisted query should target at execution time.")
            .ResolveAsync(ResolveStreamDataQueryAsync);

        Field<NonNullGraphType<StreamDataTransientQuery>>("TransientStreamDataQuery")
            .Description("Transient stream-data queries")
            .Resolve(_ => new { });

        Connection<NonNullGraphType<StreamDataEntityGenericDtoType>>("StreamDataEntities")
            .Description("Generic stream-data entity connection. Supply the CK type at query time.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.ArchiveRtIdArg, "CkArchive runtime id whose table should be queried.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg, "CK type to query")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.ColumnPathsArg, "Attribute paths to project")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument, "Time filter and limit")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg, "Field-level comparison filters")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Scope to specific runtime entity IDs")
            .ResolveAsync(ResolveStreamDataEntitiesAsync);
    }

    private async Task<object?> ResolveStreamDataEntitiesAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            var gql = (GraphQlUserContext)arg.UserContext;
            var repo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var ckTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkIdArg);
            var archiveRtId = arg.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);
            var columnPaths = arg.GetArgument<IEnumerable<string>>(Statics.ColumnPathsArg).ToList();

            var archiveSnapshot = await gql.TenantContext.GetCkArchiveRuntimeStore().GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            var fieldResolver = new StreamDataFieldResolver(archiveSnapshot.Columns.Select(c => c.Path));
            arg.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? sortDtos);
            arg.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtos);
            arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var execArgs = arg.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);
            var fieldFilters = fieldFilterDtos?.ToList();

            StreamDataFieldValidation.ValidateStreamDataFields(
                fieldResolver, columnPaths,
                sortDtos?.Select(s => s.AttributePath),
                fieldFilters?.Where(f => f.ComparisonValue != null).Select(f => f.AttributePath));

            var columnMappings = fieldResolver.ResolveToMappings(columnPaths);

            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(ckTypeId)
                .WithColumns(columnPaths)
                .WithRtIds(rtIds?.ToList())
                .WithTimeRange(execArgs?.From, execArgs?.To)
                .WithLimit(execArgs?.Limit)
                .WithSortOrders(StreamDataGraphQlMapper.MapSortOrders(sortDtos))
                .WithFieldFilters(StreamDataGraphQlMapper.MapFieldFilters(fieldFilters))
                .WithPagination(arg.GetOffset(), arg.First);

            var result = await repo.ExecuteQueryAsync(archiveRtId, options);
            var rows = result.Rows
                .Select(r => StreamDataQueryRowDto.FromStreamDataRow(r, columnMappings))
                .ToList();
            var offset = arg.GetOffset().GetValueOrDefault(0);
            return ConnectionUtils.ToOctoConnection(rows, arg,
                rows.Count != 0 ? offset : 0, (int)result.TotalCount);
        }
        catch (Exception e) { return arg.HandleException(e); }
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
            var archiveRtId = arg.GetArgument<OctoObjectId>(Statics.ArchiveRtIdArg);
            var loaded = await tenantRepository.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                sessionAccessor.Session, queryRtId)
                ?? throw AssetRepositoryException.RtQueryNotFound(queryRtId);

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
