using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Root query type for transient runtime queries
/// </summary>
public sealed class RtTransientQuery: ObjectGraphType
{
    private readonly ILogger<RtTransientQuery> _logger;

    /// <inheritdoc />
    public RtTransientQuery(ILogger<RtTransientQuery> logger)
    {
        _logger = logger;
        Name = "RtTransient";

        Connection<NonNullGraphType<RtTransientQueryDtoType>>("Simple")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.ColumnPathsArg,
                "The column paths to include in the result.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveTransientRtSimpleQuery);

        Connection<NonNullGraphType<RtTransientQueryDtoType>>("Aggregation")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnInputDtoType>>>>(Statics.ColumnPathsArg,
                "The column paths including aggregation type to include in the result.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveTransientRtAggregationQuery);

        Connection<NonNullGraphType<RtTransientQueryDtoType>>("GroupingAggregation")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.GroupByColumnPathsArg,
                "The attribute paths to group by.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnInputDtoType>>>>(Statics.ColumnPathsArg,
                "The column paths including aggregation type to include in the result.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveTransientRtGroupingAggregationQuery);
    }

    private object ResolveTransientRtSimpleQuery(IResolveConnectionContext<object?> arg)
    {
        _logger.LogDebug("GraphQL query handling for transient runtime query started");

        var ckCacheService = arg.GetCkCacheService();

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var queryCkTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

        var columnPaths = arg.GetArgument<IEnumerable<string>>(Statics.ColumnPathsArg);
        var columnPathList = columnPaths.ToList();

        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, queryCkTypeId, columnPathList);
        var resolvedColumns = columnPathList
            .Select(cp => (Path: cp, Column: QueryColumnPathResolver.TryResolveColumn(typeQueryColumnPaths, cp)))
            .ToList();
        var invalidColumnPaths = resolvedColumns.Where(rc => rc.Column == null).Select(rc => rc.Path).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths);
        }

        var selectedTypeQueryColumns = resolvedColumns
            .Select(rc => Tuple.Create(rc.Column!, AggregationTypesDto.None))
            .ToList();

        var queryOptions = arg.GetQueryOptions();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtTransientQueryDtoType.CreateTransientRtQueryDto(RtTransientQueryDtoType.QueryType.Standard, queryCkTypeId, queryOptions, selectedTypeQueryColumns)],
            arg,
            0, 1);
    }

    private object ResolveTransientRtAggregationQuery(IResolveConnectionContext<object?> arg)
    {
        _logger.LogDebug("GraphQL query handling for transient runtime query started");

        var ckCacheService = arg.GetCkCacheService();

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var queryCkTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

        var columnPaths = arg.GetArgument<IEnumerable<RtQueryColumnInputDto>>(Statics.ColumnPathsArg);
        var columnPathList = columnPaths.ToList();

        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, queryCkTypeId,
            columnPathList.Select(c => c.AttributePath).ToList());
        var invalidColumnPaths = columnPathList.Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths.Select(p=> p.AttributePath).ToList());
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Join(columnPathList,
                ckTypeQueryColumn => ckTypeQueryColumn.Path,
                column => column.AttributePath,
                (ckTypeQueryColumn, column) => Tuple.Create(ckTypeQueryColumn, MapAggregationType(column.AggregationType)))
            .ToList();

        var queryOptions = arg.GetQueryOptions();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtTransientQueryDtoType.CreateTransientRtQueryDto(RtTransientQueryDtoType.QueryType.Aggregation, queryCkTypeId, queryOptions, selectedTypeQueryColumns)], arg,
            0, 1);
    }

    private object ResolveTransientRtGroupingAggregationQuery(IResolveConnectionContext<object?> arg)
    {
        _logger.LogDebug("GraphQL query handling for transient grouping aggregation query started");

        var ckCacheService = arg.GetCkCacheService();

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var queryCkTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

        // Get groupBy column paths
        var groupByColumnPaths = arg.GetArgument<IEnumerable<string>>(Statics.GroupByColumnPathsArg);
        var groupByColumnPathList = groupByColumnPaths.ToList();

        // Get aggregation column paths
        var columnPaths = arg.GetArgument<IEnumerable<RtQueryColumnInputDto>>(Statics.ColumnPathsArg);
        var columnPathList = columnPaths.ToList();

        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, queryCkTypeId,
            groupByColumnPathList.Concat(columnPathList.Select(c => c.AttributePath)).ToList());

        // Validate groupBy column paths
        var invalidGroupByColumnPaths = groupByColumnPathList
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidGroupByColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidGroupByColumnPaths);
        }

        // Validate aggregation column paths
        var invalidColumnPaths = columnPathList
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths.Select(p => p.AttributePath).ToList());
        }

        // Map selected columns with aggregation types
        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Join(columnPathList,
                ckTypeQueryColumn => ckTypeQueryColumn.Path,
                column => column.AttributePath,
                (ckTypeQueryColumn, column) => Tuple.Create(ckTypeQueryColumn, MapAggregationType(column.AggregationType)))
            .ToList();

        // Resolve groupBy column paths to CkTypeQueryColumn objects
        var groupByTypeQueryColumns = typeQueryColumnPaths
            .Where(c => groupByColumnPathList.Contains(c.Path))
            .ToList();

        var queryOptions = arg.GetQueryOptions();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtTransientQueryDtoType.CreateTransientRtQueryDto(RtTransientQueryDtoType.QueryType.GroupingAggregation, queryCkTypeId, queryOptions, selectedTypeQueryColumns, groupByTypeQueryColumns)], arg,
            0, 1);
    }

    private static AggregationTypesDto MapAggregationType(AggregationInputTypesDto inputType)
    {
        return inputType switch
        {
            AggregationInputTypesDto.Count => AggregationTypesDto.Count,
            AggregationInputTypesDto.Sum => AggregationTypesDto.Sum,
            AggregationInputTypesDto.Average => AggregationTypesDto.Average,
            AggregationInputTypesDto.Minimum => AggregationTypesDto.Minimum,
            AggregationInputTypesDto.Maximum => AggregationTypesDto.Maximum,
            _ => throw new ArgumentOutOfRangeException(nameof(inputType), inputType, $"Unknown aggregation input type: {inputType}")
        };
    }
}