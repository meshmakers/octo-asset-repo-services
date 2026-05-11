using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Descriptor DTO for a persisted stream-data query.
/// Carries the query identity and columns, plus a <see cref="StreamDataQueryUserContext"/>
/// that holds the fully loaded entity for sub-connection dispatch.
/// </summary>
internal sealed class StreamDataQueryDto : GraphQlDto
{
    /// <summary>The runtime id of the persisted query entity.</summary>
    public required OctoObjectId QueryRtId { get; init; }

    /// <summary>The CK type the query targets.</summary>
    public required RtCkId<CkTypeId> AssociatedCkTypeId { get; init; }

    /// <summary>The output columns of the query.</summary>
    public required IReadOnlyList<RtQueryColumnDto> Columns { get; init; }
}

/// <summary>
/// User context passed through the sub-connection resolvers of
/// <see cref="StreamDataQueryDtoType"/>. Holds the fully loaded, typed query entity.
/// </summary>
internal sealed class StreamDataQueryUserContext
{
    /// <summary>The loaded <see cref="RtStreamDataQuery"/> runtime entity (concrete subtype).</summary>
    public required RtStreamDataQuery LoadedQuery { get; init; }

    /// <summary>
    /// The CkArchive runtime id whose table the persisted query should run against. Supplied by
    /// the caller at the parent connection so the persisted query (which has no archive linkage)
    /// can target a concrete archive at execution time. T10 / concept §16.
    /// </summary>
    public required OctoObjectId ArchiveRtId { get; init; }
}
