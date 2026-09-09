using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST projection of one rung of an archive family with its measured data coverage (AB#5157).
/// Returned by <c>GET streamdata/archives/{archiveRtId}/coverage</c> — the queried archive first,
/// then its transitive dependents. Mirrors the SDK <c>ArchiveCoverageDto</c> exactly; enum values
/// travel as their PascalCase names.
/// </summary>
public sealed record ArchiveCoverageRestDto(
    string ArchiveRtId,
    string? RtWellKnownName,
    bool IsBase,
    string Status,
    long? BucketSizeMs,
    string BucketAlignment,
    IReadOnlyList<string> StoredFunctions,
    DateTime? AvailableFrom,
    DateTime? AvailableTo)
{
    /// <summary>Maps an engine coverage rung to the REST projection.</summary>
    public static ArchiveCoverageRestDto From(ArchiveCoverageRung rung) => new(
        rung.ArchiveRtId.ToString(),
        rung.RtWellKnownName,
        rung.IsBase,
        rung.Status.ToString(),
        rung.BucketSizeMs,
        rung.Alignment.ToString(),
        rung.StoredFunctions.Select(f => f.ToString()).ToList(),
        rung.AvailableFrom,
        rung.AvailableTo);
}
