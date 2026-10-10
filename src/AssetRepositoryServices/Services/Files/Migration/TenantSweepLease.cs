using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Per-tenant lease of the file data sweep (AB#6175): one document per lease name in
///     <see cref="ReportingFilesMigrationConstants.LeaseCollectionName" /> of the tenant database, taken with an
///     atomic <c>findOneAndUpdate</c> upsert that only matches an expired lease (or a free slot). Every pod
///     runs the sweep at tenant start; only the lease holder works, the others skip. The holder renews the
///     lease after every batch; a crashed holder blocks the tenant for at most the lease duration.
/// </summary>
public sealed class TenantSweepLease : IAsyncDisposable
{
    private const int DuplicateKeyErrorCode = 11000;

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly string _name;
    private readonly TimeSpan _duration;
    private bool _released;

    private TenantSweepLease(IMongoCollection<BsonDocument> collection, string name, string owner, TimeSpan duration)
    {
        _collection = collection;
        _name = name;
        Owner = owner;
        _duration = duration;
    }

    /// <summary>
    ///     Unique owner id of this lease (host + random suffix).
    /// </summary>
    public string Owner { get; }

    /// <summary>
    ///     Tries to take the lease; null when another owner holds an unexpired one.
    /// </summary>
    public static async Task<TenantSweepLease?> TryAcquireAsync(IMongoDatabase database, string name,
        TimeSpan duration, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LeaseCollectionName);
        var owner = $"{Environment.MachineName}/{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", name) &
                     Builders<BsonDocument>.Filter.Lt("expiresAt", now);
        var update = Builders<BsonDocument>.Update
            .Set("owner", owner)
            .Set("acquiredAt", now)
            .Set("expiresAt", now + duration);
        try
        {
            // Matches only an expired lease; when none matches, the upsert inserts _id = name, which fails with
            // a duplicate key while an unexpired lease exists — exactly the "not acquired" case.
            await collection.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
            return new TenantSweepLease(collection, name, owner, duration);
        }
        catch (MongoCommandException ex) when (ex.Code == DuplicateKeyErrorCode)
        {
            return null;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    /// <summary>
    ///     Extends the lease; false when it was lost (expired and taken by another owner).
    /// </summary>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        var result = await _collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", _name) & Builders<BsonDocument>.Filter.Eq("owner", Owner),
            Builders<BsonDocument>.Update.Set("expiresAt", DateTime.UtcNow + _duration),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    /// <summary>
    ///     Releases the lease (only if still held by this owner).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            await _collection.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", _name) & Builders<BsonDocument>.Filter.Eq("owner", Owner))
                .ConfigureAwait(false);
        }
        catch (MongoException)
        {
            // Expires on its own.
        }
    }
}
