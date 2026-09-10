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

    /// <summary>
    /// Deprecated single-source form (AB#5157). When set, it is translated into exactly one
    /// unbounded <see cref="RollupSourceReference"/>. Mutually exclusive with <see cref="Sources"/>:
    /// the resolver rejects the payload when both or neither are supplied.
    /// </summary>
    public OctoObjectId? SourceArchiveRtId { get; set; }

    /// <summary>
    /// Multi-source declaration (AB#5157): one or more source archives, each with an optional
    /// validity span (<c>ValidFrom</c> inclusive, <c>ValidTo</c> exclusive). The lifecycle service
    /// validates the span rules, the target-type / path compatibility and the transitive cycle
    /// check before inserting.
    /// </summary>
    public List<CreateRollupSourceInputDto>? Sources { get; set; }

    public long BucketSizeMs { get; set; }
    public long WatermarkLagMs { get; set; }

    /// <summary>
    /// Bucket-boundary alignment (AB#4300). Defaults to <see cref="BucketAlignment.FixedSize"/> so
    /// existing callers that omit it keep the legacy fixed-window behaviour. Calendar variants make
    /// day / week / month / quarter / year rollups expressible and are the only ones for which
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

    /// <summary>
    /// Optional bound on the TimeWeightedAvg carry-in scan (LOCF opening state) in milliseconds
    /// (AB#4336 / decision D1). Null keeps the engine default of 35 days. Only meaningful when the
    /// aggregations include TIME_WEIGHTED_AVG; ignored otherwise.
    /// </summary>
    public long? CarryLookbackMs { get; set; }

    public List<RollupAggregationInputDto> Aggregations { get; set; } = new();
}

/// <summary>
/// One source declaration of a multi-source rollup (AB#5157). Mirrors
/// <see cref="RollupSourceReference"/>: the source archive plus an optional half-open validity span.
/// </summary>
internal sealed class CreateRollupSourceInputDto
{
    public OctoObjectId SourceArchiveRtId { get; set; }

    /// <summary>Inclusive start of the validity span; null = unbounded towards the past.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>Exclusive end of the validity span; null = unbounded towards the future.</summary>
    public DateTime? ValidTo { get; set; }
}

internal sealed class RollupAggregationInputDto
{
    public string SourcePath { get; set; } = string.Empty;
    public CkRollupFunction Function { get; set; }
    public string? TargetColumnName { get; set; }

    /// <summary>
    /// State literal a STATE_DURATION aggregation matches the source column against (AB#4336).
    /// Required for STATE_DURATION (validated at save time); ignored for every other function.
    /// </summary>
    public string? ComparisonValue { get; set; }
}
