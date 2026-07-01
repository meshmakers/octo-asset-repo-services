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
}
