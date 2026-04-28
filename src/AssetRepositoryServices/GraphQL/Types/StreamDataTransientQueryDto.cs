using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Descriptor DTO for a transient (ad-hoc) stream-data query.
/// Carries the target CK type and resolved output columns.
/// The <see cref="GraphQlDto.UserContext"/> (inherited) is set to a
/// <see cref="StreamDataTransientUserContext"/> instance for sub-connection dispatch.
/// </summary>
internal sealed class StreamDataTransientQueryDto : GraphQlDto
{
    /// <summary>The CK type the query targets.</summary>
    public required RtCkId<CkTypeId> QueryCkTypeId { get; init; }

    /// <summary>The output columns of the query.</summary>
    public required IReadOnlyList<RtQueryColumnDto> Columns { get; init; }
}

/// <summary>
/// Carries all caller-supplied arguments needed by the sub-connection resolvers
/// of <see cref="StreamDataTransientQueryDtoType"/>.
/// </summary>
internal sealed class StreamDataTransientUserContext
{
    /// <summary>Which of the four stream-data query variants to execute.</summary>
    public required StreamQueryVariant Variant { get; init; }

    /// <summary>The CkArchive runtime id whose table the query should run against. T10 / concept §16.</summary>
    public required OctoObjectId ArchiveRtId { get; init; }

    /// <summary>The CK type the query targets.</summary>
    public required RtCkId<CkTypeId> CkTypeId { get; init; }

    // Simple variant
    /// <summary>Simple/downsampling: attribute paths to project.</summary>
    public IReadOnlyList<string>? ColumnPaths { get; init; }

    // Aggregation / GroupingAggregation / Downsampling
    /// <summary>Aggregation/grouping/downsampling: column definitions with aggregation function.</summary>
    public IReadOnlyList<AggregationColumn>? AggregationColumns { get; init; }

    // GroupingAggregation
    /// <summary>Grouping aggregation: attribute paths to group by.</summary>
    public IReadOnlyList<string>? GroupByColumnPaths { get; init; }

    // Time range
    /// <summary>Start of the query time window.</summary>
    public DateTime? From { get; init; }

    /// <summary>End of the query time window.</summary>
    public DateTime? To { get; init; }

    /// <summary>Row/bucket limit (Simple = row cap, Downsampling = bucket count).</summary>
    public int? Limit { get; init; }

    // Simple variant
    /// <summary>Sort order (Simple only).</summary>
    public IReadOnlyList<SortOrderItem>? SortOrders { get; init; }

    // Common
    /// <summary>Field-level comparison filters.</summary>
    public IReadOnlyList<FieldFilter>? FieldFilters { get; init; }

    /// <summary>Scope to these entity IDs (all entities if null).</summary>
    public IReadOnlyList<OctoObjectId>? RtIds { get; init; }
}
