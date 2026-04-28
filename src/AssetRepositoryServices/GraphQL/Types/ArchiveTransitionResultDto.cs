using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Result of an archive lifecycle mutation (concept §16). Echoes back the targeted archive id,
/// the resulting status, and the transition name for client-side telemetry.
/// </summary>
internal sealed record ArchiveTransitionResultDto(
    OctoObjectId ArchiveRtId,
    CkArchiveStatus Status,
    string Transition);
