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
///         <item>cheap check: legacy entities, legacy association type fields, legacy GridFS stamps —
///             all zero means no write at all (steady state, also after System.Reporting 3.0.0);</item>
///         <item>precondition: System.Files is imported (target collection with its CK indexes);</item>
///         <item>entities, batched, one transaction per batch: <c>replaceOne</c> upsert by <c>_id</c> into
///             <c>RtEntity_SystemFilesFileSystemEntity</c> with the rewritten <c>ckTypeId</c>, delete from the
///             source; every batch is verified in the target afterwards;</item>
///         <item><c>RtAssociation</c> <c>originCkTypeId</c>/<c>targetCkTypeId</c> <c>updateMany</c> (outside the
///             transaction, like the engine's ChangeCkType) — like the next two steps only after every
///             batch verified; a mismatch logs an error, writes the audit record and leaves the rest as it is;</item>
///         <item><c>fs.files</c> owner stamp prefix rewrite — the R1 fix: the download data-permission gate
///             and the cascade delete of linked binaries both key on that stamp;</item>
///         <item>drop of the legacy collection only when it is empty;</item>
///         <item>before/after counts in the log and an audit record in <c>FilesMigrationAudit</c>.</item>
///     </list>
///     All access is raw (no CK cache): the legacy types may already be unknown to the cache.
/// </summary>
public class ReportingFilesMoveSweep
{
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
    public async Task<ReportingFilesSweepResult> SweepAsync(string tenantId, string trigger,
        CancellationToken cancellationToken = default)
    {
        var result = new ReportingFilesSweepResult { TenantId = tenantId };
        var stopwatch = Stopwatch.StartNew();
        IMongoDatabase? database = null;
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
                _logger.LogWarning(
                    "{Sweep}: tenant '{TenantId}' has legacy System.Reporting file data ({Before}) but System.Files is not imported " +
                    "(collection '{Collection}' or its indexes missing); skipping the move",
                    ReportingFilesMigrationConstants.SweepName, tenantId, result.Before,
                    FileSystemConstants.EntityCollectionName);
                result.Outcome = ReportingFilesSweepOutcome.TargetModelMissing;
                return result;
            }

            _logger.LogInformation("{Sweep}: tenant '{TenantId}' before: {Before}",
                ReportingFilesMigrationConstants.SweepName, tenantId, result.Before);

            var verified = await MoveEntitiesAsync(database, result, cancellationToken).ConfigureAwait(false);
            if (verified)
            {
                // Associations and stamps only after every batch verified: on a mismatch the state is left
                // for investigation (the next sweep retries) instead of pointing more data at entities
                // that may not have arrived.
                await RewriteAssociationsAsync(database, result, cancellationToken).ConfigureAwait(false);
                await RewriteStampsAsync(database, result, cancellationToken).ConfigureAwait(false);
                result.SourceCollectionDropped =
                    await DropSourceIfEmptyAsync(database, cancellationToken).ConfigureAwait(false);
            }

            result.After = await CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
            result.Outcome = verified ? ReportingFilesSweepOutcome.Moved : ReportingFilesSweepOutcome.CountMismatch;

            if (verified)
            {
                _logger.LogInformation(
                    "{Sweep}: tenant '{TenantId}' moved {Moved} entities ({PerType}), {Origins} association origins, " +
                    "{Targets} association targets, {Stamps} GridFS stamps; source dropped: {Dropped}; after: {After} ({DurationMs} ms)",
                    ReportingFilesMigrationConstants.SweepName, tenantId, result.TotalEntitiesMoved,
                    string.Join(", ", result.EntitiesMoved.Select(kv => $"{kv.Key}={kv.Value}")),
                    result.AssociationOriginsUpdated, result.AssociationTargetsUpdated, result.StampsUpdated,
                    result.SourceCollectionDropped, result.After, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogError(
                    "{Sweep}: tenant '{TenantId}' count mismatch, source collection kept: {Errors}; after: {After}",
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
        var names = await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", FileSystemConstants.EntityCollectionName)
            }, cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (names.Count == 0)
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

    // ------------------------------------------------------------------------------------------------
    // Steps
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Moves the legacy entities in batches, one transaction per batch, and verifies each batch in the
    ///     target. Returns false on the first unverified batch (the rest stays in the source).
    /// </summary>
    private async Task<bool> MoveEntitiesAsync(IMongoDatabase database, ReportingFilesSweepResult result,
        CancellationToken cancellationToken)
    {
        var source = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName);
        var target = database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName);
        var legacyFilter = Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.LegacyTypeIds);
        var batchSize = Math.Max(1, _options.CurrentValue.BatchSize);

        // Guard against an endless loop when another writer keeps inserting legacy documents: the
        // remainder is picked up by the next sweep.
        var maxBatches = (int)Math.Min(int.MaxValue, result.Before!.Entities / batchSize + 10);

        for (var batch = 0; batch < maxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));

            List<BsonDocument> documents;
            var movedPerType = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                documents = await source.Find(session, legacyFilter).Limit(batchSize)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (documents.Count == 0)
                {
                    await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

                var replacements = new List<WriteModel<BsonDocument>>(documents.Count);
                foreach (var document in documents)
                {
                    var legacyType = document["ckTypeId"].AsString;
                    document["ckTypeId"] = ReportingFilesMigrationConstants.TypeMap[legacyType];
                    movedPerType[legacyType] = movedPerType.GetValueOrDefault(legacyType) + 1;
                    replacements.Add(new ReplaceOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", document["_id"]), document) { IsUpsert = true });
                }

                await target.BulkWriteAsync(session, replacements, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
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

            var ids = documents.Select(d => d["_id"]).ToList();
            var arrived = await target.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.In("_id", ids) &
                Builders<BsonDocument>.Filter.In("ckTypeId", ReportingFilesMigrationConstants.TargetTypeIds),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var stillInSource = await source.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("_id", ids),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var (legacyType, count) in movedPerType)
            {
                result.EntitiesMoved[legacyType] = result.EntitiesMoved.GetValueOrDefault(legacyType) + count;
            }

            if (arrived != ids.Count || stillInSource != 0)
            {
                result.Errors.Add(
                    $"Batch {batch + 1}: {ids.Count} entities moved, {arrived} found in '{FileSystemConstants.EntityCollectionName}', " +
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

    private static async Task<bool> DropSourceIfEmptyAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var names = await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", ReportingFilesMigrationConstants.LegacyEntityCollectionName)
            }, cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (names.Count == 0)
        {
            return false;
        }

        var remaining = await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (remaining > 0)
        {
            return false;
        }

        await database.DropCollectionAsync(ReportingFilesMigrationConstants.LegacyEntityCollectionName,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WriteAuditAsync(IMongoDatabase database, ReportingFilesSweepResult result, string trigger)
    {
        try
        {
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
                { "sourceCollectionDropped", result.SourceCollectionDropped },
                { "errors", new BsonArray(result.Errors) },
                { "durationMs", result.DurationMs }
            };
            await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AuditCollectionName)
                .InsertOneAsync(record).ConfigureAwait(false);
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
