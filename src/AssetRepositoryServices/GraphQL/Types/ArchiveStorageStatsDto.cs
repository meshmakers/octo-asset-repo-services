using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ArchiveStorageStats"/> — one row per archive in the
/// <c>archivesStorageStats(rtIds)</c> bulk-stats query. Mirrors the engine contract field-for-
/// field but lives in the asset-repo namespace so the GraphQL layer doesn't expose the engine
/// record type directly to schema generation.
/// </summary>
internal sealed record ArchiveStorageStatsDto(
    OctoObjectId ArchiveRtId,
    bool TableExists,
    long RecordCount,
    long SizeBytes,
    ArchiveStorageHealth Health);
