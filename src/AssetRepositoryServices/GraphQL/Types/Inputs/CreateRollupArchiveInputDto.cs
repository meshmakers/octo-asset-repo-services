using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// Input payload for the <c>createRollupArchive</c> mutation. Carries the rollup-specific fields
/// the operator picks in the UI; the server resolves the inherited CkArchive attributes
/// (TargetCkTypeId from the source archive, Columns via <see cref="RollupColumnGenerator"/>)
/// in <see cref="IRollupArchiveLifecycleService.CreateAsync"/>. Rollup-archives concept §4 / §9.
/// </summary>
internal sealed class CreateRollupArchiveInputDto
{
    public string? RtWellKnownName { get; set; }
    public OctoObjectId SourceArchiveRtId { get; set; }
    public int BucketSizeMs { get; set; }
    public int WatermarkLagMs { get; set; }
    public List<RollupAggregationInputDto> Aggregations { get; set; } = new();
}

internal sealed class RollupAggregationInputDto
{
    public string SourcePath { get; set; } = string.Empty;
    public CkRollupFunction Function { get; set; }
    public string? TargetColumnName { get; set; }
}
