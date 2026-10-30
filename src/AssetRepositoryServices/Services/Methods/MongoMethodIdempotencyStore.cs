using System.Collections.Concurrent;
using System.Text.Json;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Idempotency;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Methods;

/// <summary>
///     MongoDB implementation of <see cref="ICkMethodIdempotencyStore" /> (AB#7824, OD-4): one document per idempotency
///     key in the collection <see cref="CollectionName" /> of the <b>tenant database</b>, so every asset repository
///     replica shares the claims of a tenant. The collection is part of the tenant backup and goes with the tenant.
///     <para>
///         <b>Atomic claim:</b> <c>_id</c> is the SHA-256 key id of <see cref="CkMethodIdempotencyKey" /> (the raw key is
///         never stored). A claim is an insert: the unique <c>_id</c> lets exactly one of any number of concurrent calls
///         win; a duplicate key means a record exists. A record past its <c>expiresAt</c> is taken over with one
///         <c>findOneAndReplace</c> whose filter matches only an expired record, so two takers never both win.
///     </para>
///     <para>
///         <b>Expiry:</b> every document carries its own <c>expiresAt</c> (claim time plus
///         <see cref="CkMethodIdempotencyLifetime.Ttl" />). The TTL index (<c>expireAfterSeconds: 0</c> on
///         <c>expiresAt</c>) is housekeeping only: readers and writers compare <c>expiresAt</c> themselves, so a record
///         the monitor has not removed yet is still treated as expired. Because the lifetime is per document, a changed
///         <c>Methods:Idempotency:Ttl</c> applies to new claims without touching the index.
///     </para>
///     <para>
///         <b>Fails closed:</b> any database problem (no admin credentials, unknown tenant, unreachable, timeout,
///         unexpected write result) is thrown as <see cref="CkMethodIdempotencyStoreException" />; the store never
///         reports a claim on a guess. It writes through the raw admin client of <see cref="ITenantMongoDatabaseProvider" />,
///         i.e. outside the request's transaction, with majority write concern.
///     </para>
///     <para>
///         <b>Stored:</b> key id, state, method id, first invocation id, the keyed fingerprint as given, key id of the
///         fingerprint, claim/lease/expiry times and, once completed, the response envelope. Never raw parameters and never
///         the raw key; the filter refuses keys for methods with a sensitive result before it reaches this store.
///     </para>
///     <para>
///         The contract's <see cref="CompleteAsync" /> and <see cref="ReleaseAsync" /> carry the key id but not the tenant;
///         the owner always runs in the process that claimed, so the store remembers the tenant of each of its own
///         in-flight claims (bounded by the record lifetime).
///     </para>
/// </summary>
internal sealed class MongoMethodIdempotencyStore(
    ITenantMongoDatabaseProvider databaseProvider,
    CkMethodIdempotencyLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<MongoMethodIdempotencyStore> logger) : ICkMethodIdempotencyStore
{
    /// <summary>Collection in the tenant database that holds the records.</summary>
    public const string CollectionName = "MethodIdempotency";

    /// <summary>Name of the TTL index on <c>expiresAt</c>.</summary>
    public const string TtlIndexName = "expiresAt_ttl";

    private const int DuplicateKeyErrorCode = 11000;
    private const int MaxClaimAttempts = 5;
    private const int SweepEvery = 256;

    // Own bound for one database round trip: the filter hands Complete/Release no cancellation token.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Owned> _owned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _indexedTenants = new(StringComparer.Ordinal);
    private int _claims;

    private readonly record struct Owned(string TenantId, DateTime ExpiresAt);

    public async Task<CkMethodIdempotencyClaimResult> ClaimAsync(CkMethodIdempotencyClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = request.Key.TenantId;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            var ct = timeout.Token;
            var collection = await GetCollectionAsync(tenantId, ct).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (Interlocked.Increment(ref _claims) % SweepEvery == 0)
            {
                SweepOwned(now);
            }

            for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
            {
                var fresh = NewRecord(request, now);
                var document = ToDocument(fresh);
                try
                {
                    await collection.InsertOneAsync(document, cancellationToken: ct).ConfigureAwait(false);
                    Remember(fresh.Id, tenantId, fresh.ExpiresAt);
                    return CkMethodIdempotencyClaimResult.ClaimedBy(fresh);
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // a record exists: live, or expired and waiting for the takeover below
                }
                catch (MongoCommandException ex) when (ex.Code == DuplicateKeyErrorCode)
                {
                }

                // Takeover of an expired record: matches only a record that is still expired, so of two takers one wins.
                var taken = await collection.FindOneAndReplaceAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", fresh.Id) &
                    Builders<BsonDocument>.Filter.Lte("expiresAt", now),
                    document, new FindOneAndReplaceOptions<BsonDocument> { IsUpsert = false },
                    ct).ConfigureAwait(false);
                if (taken != null)
                {
                    Remember(fresh.Id, tenantId, fresh.ExpiresAt);
                    return CkMethodIdempotencyClaimResult.ClaimedBy(fresh);
                }

                var existing = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", fresh.Id))
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (existing != null)
                {
                    var record = FromDocument(existing);
                    if (!record.IsExpired(now))
                    {
                        return CkMethodIdempotencyClaimResult.ExistingRecord(record);
                    }
                }

                // removed by the TTL monitor, or replaced, between the steps: decide again
                now = timeProvider.GetUtcNow().UtcDateTime;
            }

            throw new CkMethodIdempotencyStoreException(
                $"The idempotency record could not be claimed after {MaxClaimAttempts} attempts.");
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Wrap(ex);
        }
    }

    public async Task<bool> CompleteAsync(string id, Guid firstInvocationId, CkMethodIdempotencyState state,
        CkMethodResponse? response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (state == CkMethodIdempotencyState.InProgress)
        {
            throw new ArgumentException("A claim is completed as Completed or Unknown.", nameof(state));
        }

        if (state == CkMethodIdempotencyState.Completed && response is null)
        {
            throw new ArgumentException("A completed record needs the response envelope to replay.", nameof(response));
        }

        if (!_owned.TryRemove(id, out var owned))
        {
            // Not claimed by this process (or already settled): the record is not ours to complete.
            logger.LogWarning("Idempotency record {KeyId} is not owned by this process; not completed", id);
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            var collection = await GetCollectionAsync(owned.TenantId, timeout.Token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var update = Builders<BsonDocument>.Update
                .Set("state", (int)state)
                .Set("response", state == CkMethodIdempotencyState.Completed
                    ? BsonValue.Create(JsonSerializer.Serialize(response))
                    : BsonNull.Value);
            var result = await collection.UpdateOneAsync(OwnedInProgress(id, firstInvocationId, now), update,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            return result.MatchedCount == 1;
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Wrap(ex);
        }
    }

    public async Task<bool> ReleaseAsync(string id, Guid firstInvocationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_owned.TryRemove(id, out var owned))
        {
            logger.LogWarning("Idempotency record {KeyId} is not owned by this process; not released", id);
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            var collection = await GetCollectionAsync(owned.TenantId, timeout.Token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var result = await collection.DeleteOneAsync(OwnedInProgress(id, firstInvocationId, now), timeout.Token)
                .ConfigureAwait(false);
            return result.DeletedCount == 1;
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Wrap(ex);
        }
    }

    private static FilterDefinition<BsonDocument> OwnedInProgress(string id, Guid firstInvocationId, DateTime now) =>
        Builders<BsonDocument>.Filter.Eq("_id", id) &
        Builders<BsonDocument>.Filter.Eq("firstInvocationId", firstInvocationId.ToString("D")) &
        Builders<BsonDocument>.Filter.Eq("state", (int)CkMethodIdempotencyState.InProgress) &
        Builders<BsonDocument>.Filter.Gt("expiresAt", now);

    private CkMethodIdempotencyRecord NewRecord(CkMethodIdempotencyClaimRequest request, DateTime now) =>
        new()
        {
            Id = request.Key.Id,
            State = CkMethodIdempotencyState.InProgress,
            MethodId = request.Key.MethodId,
            FirstInvocationId = request.FirstInvocationId,
            Fingerprint = request.Fingerprint.ToArray(),
            FingerprintKeyId = request.FingerprintKeyId,
            ClaimedAt = now,
            LeaseUntil = request.LeaseUntil,
            ExpiresAt = now + lifetime.Ttl
        };

    private static BsonDocument ToDocument(CkMethodIdempotencyRecord record)
    {
        var document = new BsonDocument
        {
            { "_id", record.Id },
            { "state", (int)record.State },
            { "methodId", record.MethodId },
            { "firstInvocationId", record.FirstInvocationId.ToString("D") },
            { "fingerprint", new BsonBinaryData(record.Fingerprint.ToArray()) },
            { "fingerprintKeyId", record.FingerprintKeyId is null ? BsonNull.Value : record.FingerprintKeyId },
            { "claimedAt", record.ClaimedAt },
            { "leaseUntil", record.LeaseUntil },
            { "expiresAt", record.ExpiresAt },
            { "response", BsonNull.Value }
        };
        return document;
    }

    private static CkMethodIdempotencyRecord FromDocument(BsonDocument document)
    {
        var response = document.GetValue("response", BsonNull.Value);
        return new CkMethodIdempotencyRecord
        {
            Id = document["_id"].AsString,
            State = (CkMethodIdempotencyState)document["state"].ToInt32(),
            MethodId = document["methodId"].AsString,
            FirstInvocationId = Guid.Parse(document["firstInvocationId"].AsString),
            Fingerprint = document["fingerprint"].AsBsonBinaryData.Bytes,
            FingerprintKeyId = document["fingerprintKeyId"].IsBsonNull ? null : document["fingerprintKeyId"].AsString,
            ClaimedAt = document["claimedAt"].ToUniversalTime(),
            LeaseUntil = document["leaseUntil"].ToUniversalTime(),
            ExpiresAt = document["expiresAt"].ToUniversalTime(),
            Response = response.IsBsonNull
                ? null
                : JsonSerializer.Deserialize<CkMethodResponse>(response.AsString)
        };
    }

    private void Remember(string id, string tenantId, DateTime expiresAt) =>
        _owned[id] = new Owned(tenantId, expiresAt);

    private void SweepOwned(DateTime now)
    {
        foreach (var pair in _owned)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _owned.TryRemove(pair);
            }
        }
    }

    // Anything but the caller's own cancellation is a store failure (fail closed).
    private static bool IsStoreFailure(Exception ex, CancellationToken callerToken) =>
        ex is not CkMethodIdempotencyStoreException &&
        ex is not ArgumentException &&
        !(ex is OperationCanceledException && callerToken.IsCancellationRequested);

    private static CkMethodIdempotencyStoreException Wrap(Exception ex) =>
        new("The idempotency store could not decide: " + ex.GetType().Name, ex);

    private async Task<IMongoCollection<BsonDocument>> GetCollectionAsync(string tenantId,
        CancellationToken cancellationToken)
    {
        var database = await databaseProvider.TryGetDatabaseAsync(tenantId).ConfigureAwait(false)
                       ?? throw new CkMethodIdempotencyStoreException(
                           $"Tenant '{tenantId}' has no database for the idempotency store.");
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
                // Housekeeping only: readers compare expiresAt themselves.
                await collection.Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                        new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = TtlIndexName }),
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
