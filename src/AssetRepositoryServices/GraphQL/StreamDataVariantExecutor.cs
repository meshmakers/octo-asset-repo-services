using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Discriminates the four concrete stream-data query variants.
/// </summary>
internal enum StreamQueryVariant
{
    Simple,
    Aggregation,
    GroupingAggregation,
    Downsampling
}

/// <summary>
/// Carries the fully-resolved execution inputs for one stream-data query call.
/// Fields that are irrelevant to a particular variant may be left null.
/// </summary>
internal sealed class StreamQueryExecutionInput
{
    public required StreamQueryVariant Variant { get; init; }
    public required OctoObjectId ArchiveRtId { get; init; }
    public required RtCkId<CkTypeId> CkTypeId { get; init; }

    // Simple query columns (string attribute paths)
    public IReadOnlyList<string>? ColumnPaths { get; init; }

    // Aggregation / Downsampling / GroupedAggregation columns
    public IReadOnlyList<AggregationColumn>? AggregationColumns { get; init; }

    // GroupedAggregation group-by paths
    public IReadOnlyList<string>? GroupByColumnPaths { get; init; }

    // Common time/limit/scope
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int? Limit { get; init; }
    public IReadOnlyList<SortOrderItem>? SortOrders { get; init; }
    public IReadOnlyList<FieldFilter>? FieldFilters { get; init; }
    public IReadOnlyList<OctoObjectId>? RtIds { get; init; }

    // Pagination for Simple queries
    public int? Offset { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>
/// Dispatches a <see cref="StreamQueryExecutionInput"/> to the appropriate
/// <see cref="IStreamDataRepository"/> method and returns a uniform
/// <see cref="StreamDataQueryResult"/>.
/// Will also be used by the transient side in Phase 4.6.
/// </summary>
internal static class StreamDataVariantExecutor
{
    /// <summary>
    /// Executes the query described by <paramref name="i"/> against <paramref name="repo"/>.
    /// </summary>
    public static async Task<StreamDataQueryResult> ExecuteAsync(IStreamDataRepository repo,
        StreamQueryExecutionInput i)
    {
        return i.Variant switch
        {
            StreamQueryVariant.Simple => await repo.ExecuteQueryAsync(i.ArchiveRtId,
                StreamDataQueryOptions.Create()
                    .WithCkTypeId(i.CkTypeId)
                    .WithColumns(i.ColumnPaths ?? [])
                    .WithRtIds(i.RtIds)
                    .WithTimeRange(i.From, i.To)
                    .WithLimit(i.Limit)
                    .WithSortOrders(i.SortOrders)
                    .WithFieldFilters(i.FieldFilters)
                    .WithPagination(i.Offset, i.PageSize)),

            StreamQueryVariant.Aggregation => await repo.ExecuteAggregationQueryAsync(i.ArchiveRtId,
                StreamDataAggregationQueryOptions.Create()
                    .WithCkTypeId(i.CkTypeId)
                    .WithAggregationColumns(i.AggregationColumns ?? [])
                    .WithRtIds(i.RtIds)
                    .WithTimeRange(i.From, i.To)
                    .WithFieldFilters(i.FieldFilters)),

            StreamQueryVariant.GroupingAggregation => await repo.ExecuteGroupedAggregationQueryAsync(i.ArchiveRtId,
                StreamDataGroupedAggregationQueryOptions.Create()
                    .WithCkTypeId(i.CkTypeId)
                    .WithGroupByColumns(i.GroupByColumnPaths ?? [])
                    .WithAggregationColumns(i.AggregationColumns ?? [])
                    .WithRtIds(i.RtIds)
                    .WithTimeRange(i.From, i.To)
                    .WithFieldFilters(i.FieldFilters)),

            StreamQueryVariant.Downsampling => await repo.ExecuteDownsamplingQueryAsync(i.ArchiveRtId,
                StreamDataDownsamplingQueryOptions.Create()
                    .WithCkTypeId(i.CkTypeId)
                    .WithAggregationColumns(i.AggregationColumns ?? [])
                    .WithTimeRange(
                        i.From ?? throw AssetRepositoryException.InvalidStreamDataQueryParams(),
                        i.To ?? throw AssetRepositoryException.InvalidStreamDataQueryParams())
                    .WithLimit(i.Limit ?? throw AssetRepositoryException.InvalidStreamDataQueryParams())
                    .WithRtIds(i.RtIds)
                    .WithFieldFilters(i.FieldFilters)),

            _ => throw new ArgumentOutOfRangeException(nameof(i.Variant), i.Variant,
                "Unknown StreamQueryVariant")
        };
    }
}
