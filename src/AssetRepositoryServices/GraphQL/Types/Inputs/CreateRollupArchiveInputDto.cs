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
    public long BucketSizeMs { get; set; }
    public long WatermarkLagMs { get; set; }

    /// <summary>
    /// Bucket-boundary alignment (AB#4300). Defaults to <see cref="BucketAlignment.FixedSize"/> so
    /// existing callers that omit it keep the legacy fixed-window behaviour. Calendar variants make
    /// day / week / month / year rollups expressible and are the only ones for which
    /// <see cref="ReferenceTimeZone"/> has any effect.
    /// </summary>
    public BucketAlignment BucketAlignment { get; set; } = BucketAlignment.FixedSize;

    /// <summary>
    /// Optional IANA reference time-zone (e.g. <c>Europe/Vienna</c>) that aligns calendar bucket
    /// boundaries to local wall-clock time so they are DST-correct (AB#4300 / decision O6). Null keeps
    /// UTC boundaries. Ignored for <see cref="BucketAlignment.FixedSize"/>. The lifecycle service
    /// validates the id and rejects an unknown zone.
    /// </summary>
    public string? ReferenceTimeZone { get; set; }

    public List<RollupAggregationInputDto> Aggregations { get; set; } = new();
}

internal sealed class RollupAggregationInputDto
{
    public string SourcePath { get; set; } = string.Empty;
    public CkRollupFunction Function { get; set; }
    public string? TargetColumnName { get; set; }
}
