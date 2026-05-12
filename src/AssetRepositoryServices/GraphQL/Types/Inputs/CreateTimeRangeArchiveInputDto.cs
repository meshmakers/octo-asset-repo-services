using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// Input payload for the <c>createTimeRangeArchive</c> mutation. Carries the
/// archive's identifying fields, the user-picked attribute paths to capture as storage columns,
/// and the optional advisory <c>PeriodMs</c>. Concept §10.
/// </summary>
internal sealed class CreateTimeRangeArchiveInputDto
{
    public string? RtWellKnownName { get; set; }
    public string TargetCkTypeId { get; set; } = string.Empty;
    public List<ArchiveColumnSpecInputDto> Columns { get; set; } = new();
    public int? PeriodMs { get; set; }
}

/// <summary>
/// Minimal column-input shape mirroring the public <see cref="CkArchiveColumnSpec"/> record but
/// with primitive types only (no <c>OctoObjectId</c> et al.) so it round-trips cleanly through the
/// GraphQL input layer.
/// </summary>
internal sealed class ArchiveColumnSpecInputDto
{
    public string Path { get; set; } = string.Empty;
    public bool Indexed { get; set; }
    public bool Required { get; set; }
}
