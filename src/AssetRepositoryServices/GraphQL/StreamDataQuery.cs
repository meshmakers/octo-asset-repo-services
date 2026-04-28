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

    public StreamDataQuery(ILogger<StreamDataQuery> logger, IGraphTypesCache graphTypesCache)
    {
        _logger = logger;
        Name = "StreamDataModelQuery";

        Connection<NonNullGraphType<StreamDataQueryDtoType>>("StreamDataQuery")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The persisted stream-data query runtime id.")
            .ResolveAsync(ResolveStreamDataQueryAsync);

        Field<NonNullGraphType<StreamDataTransientQuery>>("TransientStreamDataQuery")
            .Description("Transient stream-data queries")
            .Resolve(_ => new { });

        Connection<NonNullGraphType<StreamDataEntityGenericDtoType>>("StreamDataEntities")
            .Description("Generic stream-data entity connection. Supply the CK type at query time.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg, "CK type to query")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.ColumnPathsArg, "Attribute paths to project")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument, "Time filter and limit")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg, "Field-level comparison filters")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Scope to specific runtime entity IDs")
            .ResolveAsync(ResolveStreamDataEntitiesAsync);

        foreach (var rtEntityDtoType in graphTypesCache.GetStreamTypes())
        {
            this.Connection<object?, IGraphType, StreamDataEntityDto>(graphTypesCache, rtEntityDtoType,
                    rtEntityDtoType.ConnectionName)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId.ToRtCkId())
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Returns entities with the given rtIds.")
                .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument, "Filter for stream data data.")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .ResolveAsync(ResolveStreamDataEntitiesByTypeAsync);
        }
    }

    private async Task<object?> ResolveStreamDataEntitiesByTypeAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for specific stream data entity type started");

            var fieldContext = FieldContext.FromContext(arg);
            var gql = (GraphQlUserContext)arg.UserContext;
            var repo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var ckTypeId = arg.GetMetadataValue<RtCkId<CkTypeId>>(Statics.CkId);
            var fieldResolver = BuildFieldResolver(arg, gql.TenantId, ckTypeId);
            var requestedType = arg.GetCkCacheService().GetRtCkType(gql.TenantId, ckTypeId);

            // Derive column paths from GraphQL selection set (the dynamic field-introspection value-add
            // of the per-type connection — clients get the columns they asked for, nothing more).
            var columnPaths = DeriveColumnPathsFromSelection(fieldContext, requestedType);

            arg.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? sortDtos);
            arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId);
            arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds);
            var execArgs = arg.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

            var rtIdList = new List<OctoObjectId>();
            if (rtId.HasValue) rtIdList.Add(rtId.Value);
            if (rtIds != null) rtIdList.AddRange(rtIds);
            if (rtIdList.Count == 0 && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<StreamDataEntityDto>(), arg);
            }

            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(ckTypeId)
                .WithColumns(columnPaths.ToList())
                .WithRtIds(rtIdList.Count > 0 ? rtIdList : null)
                .WithTimeRange(execArgs?.From, execArgs?.To)
                .WithLimit(execArgs?.Limit)
                .WithSortOrders(StreamDataGraphQlMapper.MapSortOrders(sortDtos))
                .WithPagination(arg.GetOffset(), arg.First);

            var result = await repo.ExecuteQueryAsync(default, options);

            var rows = result.Rows
                .Select(row => StreamDataEntityDtoType.CreateStreamDataEntityDto(
                    ConvertToDataPointDto(row)))
                .ToList();

            var offset = arg.GetOffset().GetValueOrDefault(0);
            return ConnectionUtils.ToOctoConnection(rows, arg,
                rows.Count != 0 ? offset : 0, (int)result.TotalCount);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveStreamDataEntitiesAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            var gql = (GraphQlUserContext)arg.UserContext;
            var repo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var ckTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkIdArg);
            var columnPaths = arg.GetArgument<IEnumerable<string>>(Statics.ColumnPathsArg).ToList();

            var fieldResolver = BuildFieldResolver(arg, gql.TenantId, ckTypeId);
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

            var result = await repo.ExecuteQueryAsync(default, options);
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
                UserContext = new StreamDataQueryUserContext { LoadedQuery = loaded }
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

    private static StreamDataFieldResolver BuildFieldResolver(
        IResolveConnectionContext<object?> ctx, string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        var ckCacheService = ctx.GetCkCacheService();
        var requestedType = ckCacheService.GetRtCkType(tenantId, ckTypeId);
        var dataStreamAttributeNames = requestedType.AllAttributes
            .Where(x => x.Value.IsDataStream)
            .Select(x => x.Value.AttributeName);
        return new StreamDataFieldResolver(dataStreamAttributeNames);
    }

    private static DataPointDto ConvertToDataPointDto(StreamDataRow row)
    {
        // row.Values is keyed by PascalCase dotted canonical form — the same form
        // RtTypeWithAttributes.GetAttributeValueOrDefault expects. Direct copy.
        return new DataPointDto(new Dictionary<string, object?>(row.Values))
        {
            RtId = row.RtId ?? OctoObjectId.Empty,
            CkTypeId = row.CkTypeId ?? throw new InvalidOperationException("CkTypeId missing on StreamDataRow"),
            Timestamp = row.Timestamp ?? default,
            RtWellKnownName = row.RtWellKnownName,
            RtCreationDateTime = row.RtCreationDateTime ?? default,
            RtChangedDateTime = row.RtChangedDateTime ?? default
        };
    }

    private static IReadOnlyList<string> DeriveColumnPathsFromSelection(
        FieldContext fieldContext, CkTypeGraph requestedType)
    {
        var itemField = fieldContext.Fields.FirstOrDefault(x => x.Name == Statics.ItemsQueryArg);
        if (itemField == null) return Array.Empty<string>();

        var dataStreamAttrs = requestedType.AllAttributes
            .Where(x => x.Value.IsDataStream)
            .ToList();

        var result = new List<string>();
        foreach (var field in itemField.Fields)
        {
            var matchingAttr = dataStreamAttrs.FirstOrDefault(kvp =>
                string.Equals(kvp.Value.AttributeName, field.Name, StringComparison.InvariantCultureIgnoreCase));
            if (matchingAttr.Value != null)
                result.Add(matchingAttr.Value.AttributeName);
        }
        return result;
    }
}
