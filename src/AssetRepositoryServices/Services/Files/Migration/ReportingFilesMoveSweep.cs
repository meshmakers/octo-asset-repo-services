using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Moves the System.Reporting file data of one tenant to System.Files (AB#6171 S3, AB#6175) — the raw
///     move verified by the S0 spike (<c>.po/ab6171-s0-spike-migrate.js</c>), as an idempotent sweep:
///     <list type="number">
///         <item>cheap check (read-only, no lease): legacy entities, legacy association type fields, legacy
///             GridFS stamps — all zero means no write at all (steady state, also after System.Reporting 3.0.0);</item>
///         <item>per-tenant lease (<see cref="TenantSweepLease" />): only one pod works, the others skip;</item>
///         <item>preconditions: System.Files is imported (target collection with its CK indexes) and no legacy
///             folder root has the well-known name of an existing System.Files root (else: abort, audit);</item>
///         <item>entities, batched, one transaction per batch: <c>replaceOne</c> upsert by <c>_id</c> into
///             <c>RtEntity_SystemFilesFileSystemEntity</c> with the rewritten <c>ckTypeId</c>, delete from the
///             source; an rtId that already exists in the target is never replaced (a stale writer re-wrote a
///             moved entity) — the target is kept, the legacy document is parked in
///             <c>FilesMigrationConflicts</c> and recorded in the audit; every batch is verified afterwards;</item>
///         <item><c>RtAssociation</c> <c>originCkTypeId</c>/<c>targetCkTypeId</c> <c>updateMany</c> (outside the
///             transaction, like the engine's ChangeCkType) and the <c>fs.files</c> owner stamp prefix rewrite
///             (R1: the download data-permission gate and the cascade delete of linked binaries key on it) —
///             both only after every batch verified;</item>
///         <item>before/after counts in the log and an audit record in <c>FilesMigrationAudit</c>.</item>
///     </list>
///     The legacy collection is never dropped: a straggler written between "empty" and "drop" would be lost.
///     All access is raw (no CK cache): the legacy types may already be unknown to the cache.
/// </summary>
public class ReportingFilesMoveSweep
{
    /// <summary>
    ///     Lease name of the sweep (one lease per tenant database).
    /// </summary>
    public const string LeaseName = ReportingFilesMigrationConstants.SweepName;

    private static readonly string LegacyStampPattern =
        "^" + Regex.Escape(ReportingFilesMigrationConstants.LegacyModelName + "/") +
        "(FileSystemItem|Folder|FolderRoot)@";

    private readonly ITenantMongoDatabaseProvider _databaseProvider;
    private readonly IOptionsMonitor<FilesMigrationOptions> _options;
    private readonly ILogger<ReportingFilesMoveSweep> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ReportingFilesMoveSweep(ITenantMongoDatabaseProvider databaseProvider,
        IOptionsMonitor<FilesMigrationOptions> options, ILogger<ReportingFilesMoveSweep> logger)
    {
        _databaseProvider = databaseProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Runs the sweep for one tenant. Never throws for data problems: failures are returned as
    ///     <see cref="ReportingFilesSweepOutcome.Failed" /> (cancellation is rethrown).
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="trigger">What triggered the run (audit only).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="lease">A lease the caller already holds; null = the sweep takes its own.</param>
    public async Task<ReportingFilesSweepResult> SweepAsync(string tenantId, string trigger,
        CancellationToken cancellationToken = default, TenantSweepLease? lease = null)
    {
        var result = new ReportingFilesSweepResult { TenantId = tenantId };
        var stopwatch = Stopwatch.StartNew();
        IMongoDatabase? database = null;
        TenantSweepLease? ownLease = null;
        try
        {
            database = await _databaseProvider.TryGetDatabaseAsync(tenantId).ConfigureAwait(false);
            if (database == null)
            {
                result.Outcome = ReportingFilesSweepOutcome.TenantNotFound;
                return result;
            }

            result.Before = await CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
            if (result.Before.IsZero)
            {
                result.Outcome = ReportingFilesSweepOutcome.NothingToDo;
                return result;
            }

            if (!await IsTargetReadyAsync(database, cancellationToken).ConfigureAwait(false))
            {
                // Read-only so far; logged (rate-limited) by the runner.
                result.Outcome = ReportingFilesSweepOutcome.TargetModelMissing;
                return result;
            }

            if (lease == null)
            {
                ownLease = await TenantSweepLease.TryAcquireAsync(database, LeaseName,
                    _options.CurrentValue.LeaseDuration, cancellationToken).ConfigureAwait(false);
                if (ownLease == null)
                {
                    _logger.LogDebug("{Sweep}: tenant '{TenantId}' is swept by another instance; skipping",
                        ReportingFilesMigrationConstants.SweepName, tenantId);
                    result.Outcome = ReportingFilesSweepOutcome.LeaseHeld;
                    return result;
                }

                lease = ownLease;
                // Another instance may have finished between the check and the lease.
                result.Before = await CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
                if (result.Before.IsZero)
                {
                    result.Outcome = ReportingFilesSweepOutcome.NothingToDo;
                    return result;
                }
            }

            result.RootConflicts.AddRange(await FindRootConflictsAsync(database, cancellationToken)
                .ConfigureAwait(false));
            if (result.RootConflicts.Count > 0)
            {
                // Logged (rate-limited) by the runner; audited once per distinct conflict set.
                result.Outcome = ReportingFilesSweepOutcome.RootConflict;
                result.Errors.AddRange(result.RootConflicts);
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                await WriteAuditAsync(database, result, trigger).ConfigureAwait(false);
                return result;
            }

            _logger.LogInformation("{Sweep}: tenant '{TenantId}' before: {Before}",
                ReportingFilesMigrationConstants.SweepName, tenantId, result.Before);

            var verified = await MoveEntitiesAsync(database, lease, result, cancellationToken).ConfigureAwait(false);
            if (verified)
            {
                // Associations and stamps only after every batch verified: on a mismatch the state is left
                // for investigation (the next sweep retries) instead of pointing more data at entities
                // that may not have arrived.
                await RewriteAssociationsAsync(database, result, cancellationToken).ConfigureAwait(false);
                await RewriteStampsAsync(database, result, cancellationToken).ConfigureAwait(false);
            }

            result.After = await CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
            result.Outcome = verified ? ReportingFilesSweepOutcome.Moved : ReportingFilesSweepOutcome.CountMismatch;

            foreach (var conflict in result.Conflicts)
            {
                _logger.LogWarning(
                    "{Sweep}: tenant '{TenantId}' legacy document {Conflict} already exists in System.Files; kept the System.Files " +
                    "version, parked the legacy document in '{Collection}'",
                    ReportingFilesMigrationConstants.SweepName, tenantId, conflict,
                    ReportingFilesMigrationConstants.ConflictCollectionName);
            }

            if (verified)
            {
                _logger.LogInformation(
                    "{Sweep}: tenant '{TenantId}' moved {Moved} entities ({PerType}), {Conflicts} conflicts, {Origins} association origins, " +
                    "{Targets} association targets, {Stamps} GridFS stamps; after: {After} ({DurationMs} ms)",
                    ReportingFilesMigrationConstants.SweepName, tenantId, result.TotalEntitiesMoved,
                    string.Join(", ", result.EntitiesMoved.Select(kv => $"{kv.Key}={kv.Value}")),
                    result.Conflicts.Count, result.AssociationOriginsUpdated, result.AssociationTargetsUpdated,
                    result.StampsUpdated, result.After, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogError(
                    "{Sweep}: tenant '{TenantId}' count mismatch, associations and stamps left as they are: {Errors}; after: {After}",
                    ReportingFilesMigrationConstants.SweepName, tenantId, string.Join("; ", result.Errors),
                    result.After);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Outcome = ReportingFilesSweepOutcome.Failed;
            result.Errors.Add(ex.Message);
            _logger.LogError(ex, "{Sweep}: tenant '{TenantId}' failed", ReportingFilesMigrationConstants.SweepName,
                tenantId);
        }
        finally
        {
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            if (ownLease != null)
            {
                await ownLease.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (database != null && result.Outcome is ReportingFilesSweepOutcome.Moved
                or ReportingFilesSweepOutcome.CountMismatch or ReportingFilesSweepOutcome.Failed)
        {
            await WriteAuditAsync(database, result, trigger).ConfigureAwait(false);
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // Checks (also used by the pre-check report)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     The cheap check: counts of legacy data. Reads only; a missing collection counts zero.
    /// </summary>
    public static async Task<ReportingFilesCounts> CountLegacyAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var entities = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName);
        var associations = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AssociationCollectionName);
        var files = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.GridFsFilesCollectionName);

        var perType = await entities.Aggregate()
            .Match(Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.LegacyTypeIds))
            .Group(new BsonDocument { { "_id", "$ckTypeId" }, { "count", new BsonDocument("$sum", 1) } })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        long CountOf(string ckTypeId) =>
            perType.FirstOrDefault(d => d["_id"] == ckTypeId)?["count"].ToInt64() ?? 0;

        var origins = await associations.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.In("originCkTypeId", ReportingFilesMigrationConstants.LegacyTypeIds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var targets = await associations.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.In("targetCkTypeId", ReportingFilesMigrationConstants.LegacyTypeIds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var stamps = await files.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Regex(ReportingFilesMigrationConstants.GridFsOwnerStampField,
                new BsonRegularExpression(LegacyStampPattern)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ReportingFilesCounts
        {
            FileSystemItems = CountOf(ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId),
            Folders = CountOf(ReportingFilesMigrationConstants.LegacyFolderCkTypeId),
            FolderRoots = CountOf(ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId),
            AssociationOrigins = origins,
            AssociationTargets = targets,
            FileStamps = stamps
        };
    }

    /// <summary>
    ///     True when System.Files is imported: its collection exists with at least one CK index besides
    ///     <c>_id</c>. Moving into a bare collection would leave the moved entities without their indexes.
    /// </summary>
    public static async Task<bool> IsTargetReadyAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        if (!await CollectionExistsAsync(database, FileSystemConstants.EntityCollectionName, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        var indexes = await (await database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName)
                .Indexes.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return indexes.Any(i => i.GetValue("name", "_id_").AsString != "_id_");
    }

    /// <summary>
    ///     System.Files entities per concrete type id.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountTargetAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var perType = await database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName).Aggregate()
            .Match(Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.TargetTypeIds))
            .Group(new BsonDocument { { "_id", "$ckTypeId" }, { "count", new BsonDocument("$sum", 1) } })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return ReportingFilesMigrationConstants.TargetTypeIds.ToDictionary(t => t,
            t => perType.FirstOrDefault(d => d["_id"] == t)?["count"].ToInt64() ?? 0, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Documents in the legacy collection whose type is none of the three moved types (derived or
    ///     unknown types), per type id. They are not moved.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountOtherLegacyTypesAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var perType = await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName)
            .Aggregate()
            .Match(Builders<BsonDocument>.Filter.Nin("ckTypeId", ReportingFilesMigrationConstants.LegacyTypeIds))
            .Group(new BsonDocument { { "_id", "$ckTypeId" }, { "count", new BsonDocument("$sum", 1) } })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return perType.ToDictionary(d => d["_id"].IsString ? d["_id"].AsString : d["_id"].ToString() ?? "<none>",
            d => d["count"].ToInt64(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Legacy folder roots whose well-known name (case-insensitive, like path resolution) equals the
    ///     well-known name of a different System.Files root — moving them would create a duplicate root.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindRootConflictsAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var projection = Builders<BsonDocument>.Projection.Include("_id").Include("rtWellKnownName");
        var legacyRoots = await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("ckTypeId", ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId) &
                  Builders<BsonDocument>.Filter.Type("rtWellKnownName", BsonType.String))
            .Project(projection).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (legacyRoots.Count == 0)
        {
            return [];
        }

        var targetRoots = await database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("ckTypeId", FileSystemConstants.FolderRootCkTypeId) &
                  Builders<BsonDocument>.Filter.Type("rtWellKnownName", BsonType.String))
            .Project(projection).ToListAsync(cancellationToken).ConfigureAwait(false);

        var conflicts = new List<string>();
        foreach (var legacy in legacyRoots)
        {
            var name = legacy["rtWellKnownName"].AsString;
            var existing = targetRoots.FirstOrDefault(t =>
                t["_id"] != legacy["_id"] &&
                string.Equals(t["rtWellKnownName"].AsString, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                conflicts.Add(
                    $"{ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId}@{legacy["_id"]} '{name}' collides with " +
                    $"{FileSystemConstants.FolderRootCkTypeId}@{existing["_id"]} '{existing["rtWellKnownName"].AsString}'");
            }
        }

        return conflicts;
    }

    /// <summary>
    ///     True when System.Reporting 3.0.0 or later is installed (the file types are gone, no writer can
    ///     produce legacy documents any more). CK model document ids are "Name-x.y.z".
    /// </summary>
    public static async Task<bool> IsReporting3InstalledAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var ids = await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.CkModelCollectionName)
            .Find(Builders<BsonDocument>.Filter.Regex("_id",
                new BsonRegularExpression("^" + Regex.Escape(ReportingFilesMigrationConstants.LegacyModelName) + "-")))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var prefixLength = ReportingFilesMigrationConstants.LegacyModelName.Length + 1;
        return ids.Select(d => d["_id"]).Where(v => v.IsString)
            .Select(v => v.AsString[prefixLength..].Split('.')[0])
            .Any(major => int.TryParse(major, out var value) && value >= 3);
    }

    internal static async Task<bool> CollectionExistsAsync(IMongoDatabase database, string name,
        CancellationToken cancellationToken)
    {
        var names = await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", name)
            }, cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return names.Count > 0;
    }

    // ------------------------------------------------------------------------------------------------
    // Steps
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Moves the legacy entities in batches, one transaction per batch, and verifies each batch in the
    ///     target. Returns false on the first unverified batch or a lost lease (the rest stays in the source).
    /// </summary>
    private async Task<bool> MoveEntitiesAsync(IMongoDatabase database, TenantSweepLease lease,
        ReportingFilesSweepResult result, CancellationToken cancellationToken)
    {
        var source = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName);
        var target = database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName);
        var conflictCollection = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.ConflictCollectionName);
        var legacyFilter = Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.LegacyTypeIds);
        var batchSize = Math.Max(1, _options.CurrentValue.BatchSize);
        var conflictCollectionReady = false;

        // Guard against an endless loop when another writer keeps inserting legacy documents: the
        // remainder is picked up by the next sweep.
        var maxBatches = (int)Math.Min(int.MaxValue, result.Before!.Entities / batchSize + 10);

        for (var batch = 0; batch < maxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await lease.RenewAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Errors.Add($"Batch {batch + 1}: the sweep lease was lost; stopped");
                return false;
            }

            using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));

            List<BsonDocument> documents;
            List<BsonValue> movedIds = [];
            var movedPerType = new Dictionary<string, long>(StringComparer.Ordinal);
            var conflicts = new List<string>();
            try
            {
                documents = await source.Find(session, legacyFilter).Limit(batchSize)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (documents.Count == 0)
                {
                    await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

                var existingInTarget = (await target.Find(session,
                            Builders<BsonDocument>.Filter.In("_id", documents.Select(d => d["_id"])))
                        .Project(Builders<BsonDocument>.Projection.Include("_id"))
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(d => d["_id"]).ToHashSet();

                if (existingInTarget.Count > 0 && !conflictCollectionReady)
                {
                    // Collections are created outside transactions (implicit creation in a transaction
                    // needs MongoDB 4.4+ and is not allowed in every deployment); retry the batch.
                    await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
                    if (!await CollectionExistsAsync(database, ReportingFilesMigrationConstants.ConflictCollectionName,
                            cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            await database.CreateCollectionAsync(ReportingFilesMigrationConstants.ConflictCollectionName,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (MongoCommandException ex) when (ex.CodeName == "NamespaceExists")
                        {
                            // Created concurrently.
                        }
                    }

                    conflictCollectionReady = true;
                    batch--;
                    continue;
                }

                var replacements = new List<WriteModel<BsonDocument>>(documents.Count);
                var parked = new List<BsonDocument>();
                foreach (var document in documents)
                {
                    var legacyType = document["ckTypeId"].AsString;
                    if (existingInTarget.Contains(document["_id"]))
                    {
                        // A stale writer re-wrote an already moved rtId: keep the System.Files version.
                        conflicts.Add($"{legacyType}@{document["_id"]}");
                        parked.Add(new BsonDocument
                        {
                            { "_id", ObjectId.GenerateNewId() },
                            { "rtId", document["_id"] },
                            { "detectedAt", DateTime.UtcNow },
                            { "rule", "target kept, legacy document parked" },
                            { "document", document }
                        });
                        continue;
                    }

                    var moved = (BsonDocument)document.DeepClone();
                    moved["ckTypeId"] = ReportingFilesMigrationConstants.TypeMap[legacyType];
                    movedIds.Add(document["_id"]);
                    movedPerType[legacyType] = movedPerType.GetValueOrDefault(legacyType) + 1;
                    // Upsert, filtered so it can never replace an existing target document.
                    replacements.Add(new ReplaceOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", document["_id"]), moved) { IsUpsert = true });
                }

                if (replacements.Count > 0)
                {
                    await target.BulkWriteAsync(session, replacements, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (parked.Count > 0)
                {
                    await conflictCollection.InsertManyAsync(session, parked, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                await source.DeleteManyAsync(session,
                    Builders<BsonDocument>.Filter.In("_id", documents.Select(d => d["_id"])),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (session.IsInTransaction)
                {
                    await session.AbortTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            var allIds = documents.Select(d => d["_id"]).ToList();
            var arrived = movedIds.Count == 0
                ? 0
                : await target.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.In("_id", movedIds) &
                    Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.TargetTypeIds),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            var stillInSource = await source.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("_id", allIds),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var (legacyType, count) in movedPerType)
            {
                result.EntitiesMoved[legacyType] = result.EntitiesMoved.GetValueOrDefault(legacyType) + count;
            }

            result.Conflicts.AddRange(conflicts);

            if (arrived != movedIds.Count || stillInSource != 0)
            {
                result.Errors.Add(
                    $"Batch {batch + 1}: {movedIds.Count} entities moved, {arrived} found in '{FileSystemConstants.EntityCollectionName}', " +
                    $"{stillInSource} still in '{ReportingFilesMigrationConstants.LegacyEntityCollectionName}'");
                return false;
            }
        }

        return true;
    }

    private static async Task RewriteAssociationsAsync(IMongoDatabase database, ReportingFilesSweepResult result,
        CancellationToken cancellationToken)
    {
        var associations = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AssociationCollectionName);
        foreach (var (legacyType, targetType) in ReportingFilesMigrationConstants.TypeMap)
        {
            var origins = await associations.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq("originCkTypeId", legacyType),
                Builders<BsonDocument>.Update.Set("originCkTypeId", targetType),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var targets = await associations.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq("targetCkTypeId", legacyType),
                Builders<BsonDocument>.Update.Set("targetCkTypeId", targetType),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            result.AssociationOriginsUpdated += origins.ModifiedCount;
            result.AssociationTargetsUpdated += targets.ModifiedCount;
        }
    }

    private static async Task RewriteStampsAsync(IMongoDatabase database, ReportingFilesSweepResult result,
        CancellationToken cancellationToken)
    {
        var files = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.GridFsFilesCollectionName);
        foreach (var (legacyType, targetType) in ReportingFilesMigrationConstants.TypeMap)
        {
            var legacyPrefix = legacyType + "@";
            var targetPrefix = targetType + "@";
            // Pipeline update: "<legacy>@<rtId>" -> "<target>@<rtId>" ($concat/$substrCP: MongoDB 4.2+).
            var stage = new BsonDocument("$set", new BsonDocument(ReportingFilesMigrationConstants.GridFsOwnerStampField,
                new BsonDocument("$concat", new BsonArray
                {
                    targetPrefix,
                    new BsonDocument("$substrCP", new BsonArray
                    {
                        "$" + ReportingFilesMigrationConstants.GridFsOwnerStampField, legacyPrefix.Length, 1000
                    })
                })));
            var update = Builders<BsonDocument>.Update.Pipeline(
                new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>([stage]));
            var stamps = await files.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Regex(ReportingFilesMigrationConstants.GridFsOwnerStampField,
                    new BsonRegularExpression("^" + Regex.Escape(legacyPrefix))),
                update, cancellationToken: cancellationToken).ConfigureAwait(false);
            result.StampsUpdated += stamps.ModifiedCount;
        }
    }

    private async Task WriteAuditAsync(IMongoDatabase database, ReportingFilesSweepResult result, string trigger)
    {
        try
        {
            var audits = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AuditCollectionName);
            var errors = new BsonArray(result.Errors);

            if (result.Outcome == ReportingFilesSweepOutcome.RootConflict)
            {
                // A persistent condition: one record until it changes, not one per timer tick.
                var last = await audits.Find(FilterDefinition<BsonDocument>.Empty)
                    .Sort(Builders<BsonDocument>.Sort.Descending("executedAt")).Limit(1)
                    .FirstOrDefaultAsync().ConfigureAwait(false);
                if (last != null && last.GetValue("outcome", BsonNull.Value) == result.Outcome.ToString() &&
                    last.GetValue("errors", BsonNull.Value).Equals(errors))
                {
                    return;
                }
            }

            var record = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "sweep", ReportingFilesMigrationConstants.SweepName },
                { "executedAt", DateTime.UtcNow },
                { "trigger", trigger },
                { "host", Environment.MachineName },
                { "outcome", result.Outcome.ToString() },
                { "before", ToBson(result.Before) },
                { "after", ToBson(result.After) },
                { "entitiesMoved", new BsonDocument(result.EntitiesMoved.Select(kv => new BsonElement(kv.Key, kv.Value))) },
                { "entitiesMovedTotal", result.TotalEntitiesMoved },
                { "associationOriginsUpdated", result.AssociationOriginsUpdated },
                { "associationTargetsUpdated", result.AssociationTargetsUpdated },
                { "stampsUpdated", result.StampsUpdated },
                { "conflicts", new BsonArray(result.Conflicts) },
                { "errors", errors },
                { "durationMs", result.DurationMs }
            };
            await audits.InsertOneAsync(record).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The move itself is done and logged; a lost audit record must not turn it into a failure.
            _logger.LogError(ex, "{Sweep}: tenant '{TenantId}' audit record could not be written",
                ReportingFilesMigrationConstants.SweepName, result.TenantId);
        }
    }

    private static BsonValue ToBson(ReportingFilesCounts? counts)
    {
        if (counts == null)
        {
            return BsonNull.Value;
        }

        return new BsonDocument
        {
            { "fileSystemItems", counts.FileSystemItems },
            { "folders", counts.Folders },
            { "folderRoots", counts.FolderRoots },
            { "associationOrigins", counts.AssociationOrigins },
            { "associationTargets", counts.AssociationTargets },
            { "fileStamps", counts.FileStamps }
        };
    }
}
