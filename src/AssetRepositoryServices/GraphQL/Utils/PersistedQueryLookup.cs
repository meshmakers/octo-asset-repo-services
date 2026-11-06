using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8273: loads a persisted query (<c>System/PersistentQuery</c> and every type derived from it) by its runtime id
///     for execution, and treats a deleted query as missing.
/// </summary>
/// <remarks>
///     The default delete of the GraphQL mutations (and of a blueprint update that removes seed data) archives the entity:
///     the document stays in its collection with <see cref="RtState.Archived" />. Queries over the collection filter
///     archived entities out, but the lookup by id (<see cref="IRuntimeRepository.GetRtEntityByRtIdAsync{TEntity}" />)
///     returns the document whatever its state. Without this check a deleted query kept running for every caller who knew
///     its rtId (<c>streamData.streamDataQuery</c>, <c>runtime.runtimeQuery</c> and the query-row mutations), while the
///     query lists no longer showed it. There is no cache of query definitions: every execution reads the stored entity,
///     so an update is visible at once on every replica.
/// </remarks>
internal static class PersistedQueryLookup
{
    /// <summary>
    ///     Returns the stored query, or <c>null</c> when no entity with the id exists or it has been deleted (archived).
    /// </summary>
    internal static async Task<TEntity?> GetActiveAsync<TEntity>(IRuntimeRepository repository, IOctoSession session,
        OctoObjectId queryRtId)
        where TEntity : RtEntity, new()
    {
        var entity = await repository.GetRtEntityByRtIdAsync<TEntity>(session, queryRtId).ConfigureAwait(false);
        return entity is null || entity.RtState == RtState.Archived ? null : entity;
    }
}
