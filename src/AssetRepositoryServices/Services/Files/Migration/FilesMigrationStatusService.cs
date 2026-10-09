using System.Buffers;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Pre-check report of the file data migration (R4, design §6.2): legacy counts, orphans, and every
///     entity outside the file collections whose stored document names a System.Reporting file type
///     literally. Read-only.
///     <para>
///         <b>Literal scan:</b> every <c>RtEntity_*</c> collection except the two file collections is read
///         as raw BSON and searched byte-wise for <c>System.Reporting/</c> (also the versioned form
///         <c>System.Reporting-x.y.z/</c>) followed by a file type name. BSON stores strings as UTF-8, so
///         the search finds the literal in any string value at any depth (pipeline YAML, policy type lists,
///         query type ids, record fields, UI JSON) without knowing the schema. Limits: it reads every
///         entity document once (cost grows with the tenant's entity count — the sweep therefore runs it
///         only when it finds legacy data); strings stored outside RtEntity collections (GridFS contents,
///         CK model collections, RtAssociation) and inside binary values are not scanned.
///     </para>
/// </summary>
public class FilesMigrationStatusService
{
    private const int OrphanBatchSize = 1000;
    private const int RecentSweepCount = 10;

    private static readonly byte[] LegacyModelBytes =
        Encoding.UTF8.GetBytes(ReportingFilesMigrationConstants.LegacyModelName);

    private static readonly HashSet<string> ExcludedCollections = new(StringComparer.Ordinal)
    {
        ReportingFilesMigrationConstants.LegacyEntityCollectionName,
        FileSystemConstants.EntityCollectionName
    };

    private readonly ITenantMongoDatabaseProvider _databaseProvider;
    private readonly ReportingFilesSweepTracker _tracker;
    private readonly IOptionsMonitor<FilesMigrationOptions> _options;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public FilesMigrationStatusService(ITenantMongoDatabaseProvider databaseProvider,
        ReportingFilesSweepTracker tracker, IOptionsMonitor<FilesMigrationOptions> options)
    {
        _databaseProvider = databaseProvider;
        _tracker = tracker;
        _options = options;
    }

    /// <summary>
    ///     Builds the report, or returns null when the tenant does not exist.
    /// </summary>
    public async Task<FilesMigrationStatusDto?> GetStatusAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        var database = await _databaseProvider.TryGetDatabaseAsync(tenantId).ConfigureAwait(false);
        if (database == null)
        {
            return null;
        }

        var options = _options.CurrentValue;
        var legacy = await ReportingFilesMoveSweep.CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
        var targetReady = await ReportingFilesMoveSweep.IsTargetReadyAsync(database, cancellationToken)
            .ConfigureAwait(false);
        var target = targetReady
            ? await ReportingFilesMoveSweep.CountTargetAsync(database, cancellationToken).ConfigureAwait(false)
            : ReportingFilesMigrationConstants.TargetTypeIds.ToDictionary(t => t, _ => 0L);
        var (orphanCount, orphans) = await FindOrphansAsync(database, Math.Max(0, options.MaxReportedOrphans),
            cancellationToken).ConfigureAwait(false);
        var scan = await ScanLiteralReferencesAsync(database, Math.Max(0, options.MaxReportedReferences),
            cancellationToken).ConfigureAwait(false);
        var recent = await ReadRecentSweepsAsync(database, cancellationToken).ConfigureAwait(false);

        return new FilesMigrationStatusDto
        {
            TenantId = tenantId,
            CheckedAt = DateTime.UtcNow,
            Legacy = legacy,
            TargetModelReady = targetReady,
            Target = target,
            OrphanCount = orphanCount,
            Orphans = orphans,
            LiteralReferenceCount = scan.Count,
            LiteralReferences = scan.References,
            ScannedCollections = scan.Collections,
            ScannedDocuments = scan.Documents,
            StragglerSweepPending = _tracker.IsPending(tenantId),
            RecentSweeps = recent
        };
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Legacy files and folders without a ParentChild association as origin (AB#4175 class). They move
    ///     as they are; the report lists them for clean-up.
    /// </summary>
    private static async Task<(long Count, IReadOnlyList<string> Sample)> FindOrphansAsync(IMongoDatabase database,
        int maxSample, CancellationToken cancellationToken)
    {
        var entities = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.LegacyEntityCollectionName);
        var associations = database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AssociationCollectionName);

        var filter = Builders<BsonDocument>.Filter.In("ckTypeId", new[]
        {
            ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId,
            ReportingFilesMigrationConstants.LegacyFolderCkTypeId
        });
        var projection = Builders<BsonDocument>.Projection.Include("_id").Include("ckTypeId");

        long count = 0;
        var sample = new List<string>();
        using var cursor = await entities.Find(filter).Project(projection)
            .ToCursorAsync(cancellationToken).ConfigureAwait(false);
        var batch = new List<BsonDocument>(OrphanBatchSize);

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            var parented = (await (await associations.DistinctAsync<BsonValue>("originRtId",
                        Builders<BsonDocument>.Filter.In("originRtId", batch.Select(d => d["_id"])) &
                        Builders<BsonDocument>.Filter.Eq("associationRoleId", FileSystemConstants.ParentChildRoleId),
                        cancellationToken: cancellationToken).ConfigureAwait(false))
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet();
            foreach (var document in batch.Where(d => !parented.Contains(d["_id"])))
            {
                count++;
                if (sample.Count < maxSample)
                {
                    sample.Add($"{document["ckTypeId"].AsString}@{document["_id"]}");
                }
            }

            batch.Clear();
        }

        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                batch.Add(document);
                if (batch.Count >= OrphanBatchSize)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }
        }

        await FlushAsync().ConfigureAwait(false);
        return (count, sample);
    }

    private sealed record ScanResult(long Count, IReadOnlyList<LegacyTypeReferenceDto> References, int Collections,
        long Documents);

    private static async Task<ScanResult> ScanLiteralReferencesAsync(IMongoDatabase database, int maxReferences,
        CancellationToken cancellationToken)
    {
        var collectionNames = await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions
                {
                    Filter = new BsonDocument("name", new BsonRegularExpression("^RtEntity_"))
                }, cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        long count = 0;
        long documents = 0;
        var collections = 0;
        var references = new List<LegacyTypeReferenceDto>();

        foreach (var collectionName in collectionNames.Where(n => !ExcludedCollections.Contains(n)).Order(StringComparer.Ordinal))
        {
            collections++;
            var collection = database.GetCollection<RawBsonDocument>(collectionName);
            using var cursor = await collection.FindAsync(FilterDefinition<RawBsonDocument>.Empty,
                new FindOptions<RawBsonDocument> { BatchSize = 500 }, cancellationToken).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var document in cursor.Current)
                {
                    using (document)
                    {
                        documents++;
                        var literals = FindLiteralsInRawDocument(document);
                        if (literals.Count == 0)
                        {
                            continue;
                        }

                        count++;
                        if (references.Count < maxReferences)
                        {
                            references.Add(BuildReference(collectionName, document, literals));
                        }
                    }
                }
            }
        }

        return new ScanResult(count, references, collections, documents);
    }

    private static IReadOnlySet<string> FindLiteralsInRawDocument(RawBsonDocument document)
    {
        var slice = document.Slice;
        var length = slice.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            slice.GetBytes(0, buffer, 0, length);
            return FindLiterals(buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Finds <c>System.Reporting/&lt;Type&gt;</c> and <c>System.Reporting-x.y.z/&lt;Type&gt;</c> for the
    ///     reported file type names in UTF-8 bytes. Returns the normalized ids (<c>System.Reporting/Type</c>).
    /// </summary>
    internal static IReadOnlySet<string> FindLiterals(ReadOnlySpan<byte> bytes)
    {
        HashSet<string>? found = null;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var index = bytes[offset..].IndexOf(LegacyModelBytes);
            if (index < 0)
            {
                break;
            }

            var position = offset + index + LegacyModelBytes.Length;
            offset = position;

            // Optional version suffix "-2.3.0".
            if (position < bytes.Length && bytes[position] == (byte)'-')
            {
                position++;
                while (position < bytes.Length && (bytes[position] == (byte)'.' || char.IsAsciiDigit((char)bytes[position])))
                {
                    position++;
                }
            }

            if (position >= bytes.Length || bytes[position] != (byte)'/')
            {
                continue;
            }

            position++;
            var start = position;
            while (position < bytes.Length && (char.IsAsciiLetterOrDigit((char)bytes[position]) || bytes[position] == (byte)'_'))
            {
                position++;
            }

            var typeName = Encoding.ASCII.GetString(bytes[start..position]);
            if (ReportingFilesMigrationConstants.ReportedLegacyTypeNames.Contains(typeName))
            {
                (found ??= new HashSet<string>(StringComparer.Ordinal))
                    .Add($"{ReportingFilesMigrationConstants.LegacyModelName}/{typeName}");
            }
        }

        return found ?? (IReadOnlySet<string>)new HashSet<string>();
    }

    private static LegacyTypeReferenceDto BuildReference(string collectionName, RawBsonDocument document,
        IReadOnlySet<string> literals)
    {
        var paths = new List<string>();
        CollectPaths(document, null, paths);

        string? AsString(BsonValue? value) => value is { IsString: true } ? value.AsString : null;
        var attributes = document.GetValue("attributes", BsonNull.Value);

        return new LegacyTypeReferenceDto
        {
            CollectionName = collectionName,
            RtId = document.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty,
            CkTypeId = AsString(document.GetValue("ckTypeId", BsonNull.Value)),
            RtWellKnownName = AsString(document.GetValue("rtWellKnownName", BsonNull.Value)),
            RtBlueprintSource = attributes is BsonDocument attributeDocument
                ? AsString(attributeDocument.GetValue("rtBlueprintSource", BsonNull.Value))
                : null,
            Literals = literals.Order(StringComparer.Ordinal).ToList(),
            FieldPaths = paths
        };
    }

    private static void CollectPaths(BsonValue value, string? path, List<string> paths)
    {
        switch (value)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    CollectPaths(element.Value, path == null ? element.Name : $"{path}.{element.Name}", paths);
                }

                break;
            case BsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    CollectPaths(array[i], $"{path}[{i}]", paths);
                }

                break;
            case BsonString text when path != null:
                if (FindLiterals(Encoding.UTF8.GetBytes(text.Value)).Count > 0)
                {
                    paths.Add(path);
                }

                break;
        }
    }

    private static async Task<IReadOnlyList<FilesMigrationAuditDto>> ReadRecentSweepsAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var records = await database.GetCollection<BsonDocument>(ReportingFilesMigrationConstants.AuditCollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("executedAt"))
            .Limit(RecentSweepCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return records.Select(r => new FilesMigrationAuditDto
        {
            ExecutedAt = r.GetValue("executedAt", BsonNull.Value) is BsonDateTime executedAt
                ? executedAt.ToUniversalTime()
                : default,
            Trigger = r.GetValue("trigger", BsonNull.Value) is BsonString trigger ? trigger.Value : null,
            Host = r.GetValue("host", BsonNull.Value) is BsonString host ? host.Value : null,
            Outcome = r.GetValue("outcome", BsonNull.Value) is BsonString outcome ? outcome.Value : null,
            EntitiesMoved = r.GetValue("entitiesMovedTotal", 0).ToInt64(),
            AssociationFieldsUpdated = r.GetValue("associationOriginsUpdated", 0).ToInt64() +
                                       r.GetValue("associationTargetsUpdated", 0).ToInt64(),
            StampsUpdated = r.GetValue("stampsUpdated", 0).ToInt64(),
            SourceCollectionDropped = r.GetValue("sourceCollectionDropped", false).ToBoolean(),
            Errors = r.GetValue("errors", new BsonArray()) is BsonArray errors
                ? errors.Select(e => e.ToString() ?? string.Empty).ToList()
                : []
        }).ToList();
    }
}
