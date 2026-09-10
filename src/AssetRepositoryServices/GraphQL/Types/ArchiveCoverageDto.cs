using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// One row of the <c>coverageFor(rtId)</c> GraphQL query (AB#5157): an archive of a resolution
/// family — the queried archive first, then its transitive dependents — with its grain and the
/// MEASURED data coverage (<see cref="AvailableFrom"/> / <see cref="AvailableTo"/>, both null when the
/// archive holds no data). Mirrors <see cref="ArchiveCoverageRung"/>.
/// </summary>
internal sealed record ArchiveCoverageDto(
    OctoObjectId ArchiveRtId,
    string? RtWellKnownName,
    bool IsBase,
    CkArchiveStatus Status,
    long? BucketSizeMs,
    BucketAlignment BucketAlignment,
    IReadOnlyList<CkRollupFunction> StoredFunctions,
    DateTime? AvailableFrom,
    DateTime? AvailableTo)
{
    /// <summary>Maps an engine coverage rung to the GraphQL projection.</summary>
    public static ArchiveCoverageDto From(ArchiveCoverageRung rung) => new(
        rung.ArchiveRtId,
        rung.RtWellKnownName,
        rung.IsBase,
        rung.Status,
        rung.BucketSizeMs,
        rung.Alignment,
        rung.StoredFunctions.ToList(),
        rung.AvailableFrom,
        rung.AvailableTo);
}
