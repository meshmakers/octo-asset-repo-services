using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Per-rollup metadata the studio's query editor needs to populate the column / aggregation
/// pickers for stream-data queries.
/// </summary>
/// <param name="RtId">Rollup archive runtime id (echo of the request argument).</param>
/// <param name="BucketSizeMs">
/// Native bucket size of this rollup in milliseconds — drives the bucket-alignment warning when
/// the operator picks a downsampling target. Concept-time-range §7.
/// </param>
/// <param name="LogicalSourcePaths">
/// Distinct logical CK-attribute paths the rollup aggregates over. For rollups directly on raw /
/// time-range archives these are simply the spec source paths; for cascade rollups (rollup over
/// rollup) the chain walker reverses the physical-storage column names back to the original
/// CK attribute path. See <c>RollupLogicalPathResolver</c>.
/// </param>
internal sealed record RollupQueryMetadataDto(
    OctoObjectId RtId,
    long BucketSizeMs,
    IReadOnlyList<string> LogicalSourcePaths);
