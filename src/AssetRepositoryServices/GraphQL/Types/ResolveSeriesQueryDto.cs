using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// The <c>resolveSeriesQuery</c> routing decision (AB#4290): which archive to query, at what
/// effective bucket width, how many points to expect, with which reducer, plus a signal. The caller
/// then runs the existing downsampling query against <see cref="ArchiveRtId"/> with
/// <c>limit = Points</c> and the column aggregation set to <see cref="ReducingFunction"/>.
/// Mirrors <see cref="SeriesResolutionResult"/>.
/// </summary>
internal sealed record ResolveSeriesQueryDto(
    OctoObjectId ArchiveRtId,
    long EffectiveBucketMs,
    int Points,
    CkRollupFunction ReducingFunction,
    SeriesResolutionSignal Signal,
    int? ActualPoints,
    string? Diagnostic);
