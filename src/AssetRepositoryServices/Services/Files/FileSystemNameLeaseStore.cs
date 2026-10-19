using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     AB#6885: cross-process half of the sibling-name lock (<see cref="FileSystemNameLocks" />). A lease is one
///     document per lock key; whoever holds an unexpired one owns the name below that parent, whatever replica
///     the request ran on.
/// </summary>
public interface IFileSystemNameLeaseStore
{
    /// <summary>
    ///     How long a lease lives without being released. It must outlast the longest session transaction (MongoDB
    ///     aborts a transaction after 60 s by default), because a lease is not renewed while it is held.
    /// </summary>
    TimeSpan LeaseDuration { get; }

    /// <summary>
    ///     Takes the lease of <paramref name="key" /> in the tenant, or returns false when another owner holds an
    ///     unexpired one. Returns true without a lease when the store cannot serve the tenant (no database
    ///     access): the caller then relies on the in-process lock alone.
    /// </summary>
    Task<bool> TryAcquireAsync(string tenantId, string key, string owner, CancellationToken cancellationToken);

    /// <summary>
    ///     Releases the lease if <paramref name="owner" /> still holds it; never throws.
    /// </summary>
    Task ReleaseAsync(string tenantId, string key, string owner);
}

/// <summary>
///     MongoDB implementation: collection <c>FileSystemNameLease</c> of the tenant database, taken with an atomic
///     <c>findOneAndUpdate</c> upsert (matches only an expired lease; the upsert's duplicate key on <c>_id</c> is the
///     "held by someone else" answer). Uses the raw admin client of <see cref="ITenantMongoDatabaseProvider" />, i.e.
///     a connection outside the request's transaction — the lease is visible to other replicas at once and is
///     released after the request's commit or abort.
/// </summary>
internal sealed class MongoFileSystemNameLeaseStore(
    ITenantMongoDatabaseProvider databaseProvider,
    ILogger<MongoFileSystemNameLeaseStore> logger) : IFileSystemNameLeaseStore
{
    /// <summary>
    ///     Collection in the tenant database that holds the leases.
    /// </summary>
    public const string CollectionName = "FileSystemNameLease";

    private const int DuplicateKeyErrorCode = 11000;

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    private readonly HashSet<string> _indexedTenants = new(StringComparer.Ordinal);
    private int _unavailableLogged;

    public TimeSpan LeaseDuration => Duration;

    public async Task<bool> TryAcquireAsync(string tenantId, string key, string owner,
        CancellationToken cancellationToken)
    {
        var collection = await GetCollectionAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (collection == null)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", key) &
                     Builders<BsonDocument>.Filter.Lt("expiresAt", now);
        var update = Builders<BsonDocument>.Update
            .Set("owner", owner)
            .Set("acquiredAt", now)
            .Set("expiresAt", now + Duration);
        try
        {
            await collection.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoCommandException ex) when (ex.Code == DuplicateKeyErrorCode)
        {
            return false;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(string tenantId, string key, string owner)
    {
        try
        {
            var collection = await GetCollectionAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
            if (collection == null)
            {
                return;
            }

            await collection.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", key) & Builders<BsonDocument>.Filter.Eq("owner", owner))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or InvalidOperationException)
        {
            // The lease expires on its own.
            logger.LogWarning(ex, "Could not release the file system name lease {Key} in tenant {TenantId}", key,
                tenantId);
        }
    }

    private async Task<IMongoCollection<BsonDocument>?> GetCollectionAsync(string tenantId,
        CancellationToken cancellationToken)
    {
        IMongoDatabase? database;
        try
        {
            database = await databaseProvider.TryGetDatabaseAsync(tenantId).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // No MongoDB admin credentials configured: the in-process lock still serialises one replica.
            if (Interlocked.Exchange(ref _unavailableLogged, 1) == 0)
            {
                logger.LogWarning(ex,
                    "Cross-replica file system name leases are unavailable; only the in-process name lock applies");
            }

            return null;
        }

        if (database == null)
        {
            return null;
        }

        var collection = database.GetCollection<BsonDocument>(CollectionName);
        bool needsIndex;
        lock (_indexedTenants)
        {
            needsIndex = _indexedTenants.Add(tenantId);
        }

        if (needsIndex)
        {
            try
            {
                // Housekeeping only: an abandoned lease (crashed replica) is removed by the TTL monitor. Expired
                // leases are taken over by TryAcquireAsync whether or not the monitor has run.
                await collection.Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                        new CreateIndexOptions { ExpireAfter = TimeSpan.FromMinutes(10), Name = "expiresAt_ttl" }),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (MongoException ex)
            {
                lock (_indexedTenants)
                {
                    _indexedTenants.Remove(tenantId);
                }

                logger.LogWarning(ex, "Could not create the TTL index of {Collection} in tenant {TenantId}",
                    CollectionName, tenantId);
            }
        }

        return collection;
    }
}
