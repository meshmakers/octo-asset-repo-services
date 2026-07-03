using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// Input payload for the <c>resolveSeriesQuery</c> query (AB#4290). Identifies a logical series by
/// its base archive plus optional rtId / OBIS scope, a time window, a target point count, and the
/// caller-supplied aggregation semantics. The server picks the archive to query; it does not run
/// the query. Mirrors <see cref="SeriesResolutionRequest"/>.
/// </summary>
internal sealed class ResolveSeriesQueryInputDto
{
    public OctoObjectId BaseArchiveRtId { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TargetPoints { get; set; }
    public CkRollupFunction RequiredAggregation { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public List<OctoObjectId>? RtIds { get; set; }
    public string? ObisFilter { get; set; }

    /// <summary>
    /// Optional IANA time zone (e.g. <c>Europe/Vienna</c>) the query is resolved in (AB#4190). Aligns
    /// calendar (day/week/month/year) rungs to that zone's DST-correct civil boundaries; null ⇒ UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// How civil boundaries are resolved when the series spans multiple reference time zones
    /// (AB#4190). Defaults to <see cref="SeriesComparisonPolicy.PerQuery"/>.
    /// </summary>
    public SeriesComparisonPolicy ComparisonPolicy { get; set; } = SeriesComparisonPolicy.PerQuery;
}
