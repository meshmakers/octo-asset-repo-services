using System.Globalization;
using System.Linq;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — the CrateDB/Mongo primitives every multi-source rollup test needs: provisioning raw,
/// time-range and rollup archives on the system tenant, writing points and windows into them,
/// driving the forward orchestrator or a manual recompute, and reading the resulting buckets back
/// out of CrateDB.
/// <para>
/// Shared by <see cref="RollupMultiSourceTests"/>, <see cref="RollupMultiSourceRecomputeTests"/>
/// and <see cref="RollupRecomputeGenerationPointerTests"/> so a ladder is described the same way
/// everywhere; each test builds its OWN archives (GUID-suffixed names) and never writes into the
/// fixture's canonical archive.
/// </para>
/// </summary>
internal sealed class MultiSourceArchiveBuilder(StreamDataFixture fixture)
{
    /// <summary>
    /// CK attribute path of the default column every ladder aggregates. Base archives declare their
    /// columns by CK path, and archive activation resolves that path against the CK model
    /// case-SENSITIVELY — a lower-cased spelling is rejected outright.
    /// </summary>
    public const string VoltagePath = "Voltage";

    /// <summary>Physical CrateDB column behind <see cref="VoltagePath"/> — the insert key.</summary>
    public const string BaseColumn = "voltage";

    /// <summary>
    /// The physical aggregate column every rung in these ladders materialises — the generated
    /// target column of a SUM over <see cref="VoltagePath"/>. A rollup source carries its storage
    /// column names as its declared paths, so this is also the name a rung ABOVE a rollup can
    /// address verbatim (see <see cref="CascadeSum"/>).
    /// </summary>
    public const string RollupColumn = "voltage_sum";

    /// <summary>
    /// The LOGICAL aggregation of these ladders: SUM over the CK path <see cref="VoltagePath"/>,
    /// materialised as <see cref="RollupColumn"/>. Since AB#5157 the engine resolves such a spec
    /// per source — a base archive (raw or time-range) declares <see cref="VoltagePath"/> verbatim
    /// and is read as <c>SUM("voltage")</c>, while a rollup source storing the same logical
    /// aggregation is read as <c>SUM("voltage_sum")</c>, its own target column. It is therefore the
    /// spec a rung mixing a base archive and a rollup declares (AC1, the sbeg quarter ladder).
    /// </summary>
    public static IReadOnlyList<CkRollupAggregationSpec> LogicalSum { get; } =
        [new CkRollupAggregationSpec(VoltagePath, CkRollupFunction.Sum, null)];

    /// <summary>
    /// The pre-AB#5157 chained style, still valid and still exercised: a rung whose sources are
    /// themselves rollups may name the rollups' PHYSICAL aggregate column and materialise it again
    /// under the same name. It only resolves on a rollup source — a base archive declares CK paths,
    /// never storage names.
    /// </summary>
    public static IReadOnlyList<CkRollupAggregationSpec> CascadeSum { get; } =
        [new CkRollupAggregationSpec(RollupColumn, CkRollupFunction.Sum, RollupColumn)];

    /// <summary>The system tenant context that owns every archive built here.</summary>
    public async Task<ITenantContext> TenantAsync()
    {
        var systemContext = fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    /// <summary>
    /// Creates and activates an isolated raw archive (default column <see cref="VoltagePath"/>) so
    /// its CrateDB table exists before any write.
    /// </summary>
    public async Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix, params string[] columnPaths)
    {
        var columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        foreach (var path in columnPaths.Length == 0 ? [VoltagePath] : columnPaths)
        {
            columns.Add(new RtCkArchiveColumnRecord { Path = path, Indexed = true, Required = false });
        }

        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = columns,
        };

        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    /// <summary>
    /// Creates and activates a windowed time-range archive with the declared advisory
    /// <paramref name="period"/> — the "legacy" half of every cutover ladder. The period is what
    /// the rollup validator compares against its own bucket length.
    /// </summary>
    public async Task<OctoObjectId> CreateTimeRangeArchiveAsync(
        string namePrefix, TimeSpan period, params string[] columnPaths)
    {
        var columns = (columnPaths.Length == 0 ? [VoltagePath] : columnPaths)
            .Select(p => new CkArchiveColumnSpec(p, Indexed: true, Required: false))
            .ToList();

        var store = (await TenantAsync()).GetTimeRangeArchiveRuntimeStore()!;
        var rtId = await store.InsertAsync(
            $"{namePrefix}{Guid.NewGuid():N}",
            new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            columns,
            period);

        await ActivateAsync(rtId);
        return rtId;
    }

    /// <summary>
    /// Writes raw points for one series and forces read-after-write consistency on the archive
    /// table. The column key is the physical column name (CrateDB columns are lower-cased).
    /// </summary>
    public async Task InsertPointsAsync(
        OctoObjectId archiveRtId,
        OctoObjectId seriesRtId,
        IEnumerable<(DateTime Timestamp, double Value)> points,
        string column = BaseColumn)
    {
        var repo = (await TenantAsync()).GetStreamDataRepository()!;
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);

        var dataPoints = points.Select(p => new StreamDataPoint
        {
            RtId = seriesRtId,
            CkTypeId = ckTypeId,
            Timestamp = p.Timestamp,
            RtWellKnownName = "MultiSourceSeries",
            Attributes = new Dictionary<string, object?> { [column] = p.Value },
        }).ToList();

        await repo.InsertAsync(archiveRtId, dataPoints);
        await RefreshAsync(archiveRtId);
    }

    /// <summary>
    /// Writes windowed rows (half-open <c>[From, To)</c>) for one series into a time-range archive
    /// and forces read-after-write consistency.
    /// </summary>
    public async Task InsertWindowsAsync(
        OctoObjectId archiveRtId,
        OctoObjectId seriesRtId,
        IEnumerable<(DateTime From, DateTime To, double Value)> windows,
        string column = BaseColumn)
    {
        var repo = (await TenantAsync()).GetStreamDataRepository()!;
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);

        var rows = windows.Select(w => new TimeRangeStreamDataPoint
        {
            RtId = seriesRtId,
            CkTypeId = ckTypeId,
            From = w.From,
            To = w.To,
            RtWellKnownName = "MultiSourceSeries",
            Attributes = new Dictionary<string, object?> { [column] = w.Value },
        }).ToList();

        await repo.InsertTimeRangeAsync(archiveRtId, rows);
        await RefreshAsync(archiveRtId);
    }

    /// <summary>
    /// Creates a rollup through the engine lifecycle service (which runs the aggregation, span and
    /// cycle rules) and leaves it in <c>Created</c>. Defaults to SUM over
    /// <see cref="VoltagePath"/> materialised as <see cref="RollupColumn"/> — the shape every
    /// cascade in these tests needs, because a rung above must aggregate the physical column name
    /// (<see cref="CascadeSum"/>).
    /// </summary>
    public async Task<OctoObjectId> CreateRollupAsync(
        string namePrefix,
        IReadOnlyList<RollupSourceReference> sources,
        TimeSpan bucketSize,
        IReadOnlyList<CkRollupAggregationSpec>? aggregations = null,
        BucketAlignment bucketAlignment = BucketAlignment.FixedSize,
        string? referenceTimeZone = null)
    {
        var lifecycle = (await TenantAsync()).GetRollupArchiveLifecycleService()!;
        return await lifecycle.CreateAsync(
            $"{namePrefix}{Guid.NewGuid():N}",
            sources,
            bucketSize,
            TimeSpan.Zero,
            aggregations ?? [new CkRollupAggregationSpec(VoltagePath, CkRollupFunction.Sum, null)],
            bucketAlignment,
            referenceTimeZone);
    }

    /// <summary>Drives an archive (raw, time-range or rollup) to Activated, provisioning its table.</summary>
    public async Task ActivateAsync(OctoObjectId archiveRtId) =>
        await (await TenantAsync()).GetArchiveLifecycleService()!.ActivateAsync(archiveRtId);

    /// <summary>
    /// Runs one forward orchestrator pass for exactly this rollup and returns the committed buckets.
    /// The rollup's own table is refreshed afterwards so a rung stacked on top — or a direct read —
    /// sees the rows this pass wrote.
    /// </summary>
    public async Task<int> TickAsync(OctoObjectId rollupRtId)
    {
        var committed = await (await TenantAsync()).GetRollupOrchestrator()!
            .ProcessRollupAsync(rollupRtId, CancellationToken.None);
        await RefreshAsync(rollupRtId);
        return committed;
    }

    /// <summary>
    /// Rewinds the rollup watermark so the next tick starts at a synthetic history instead of the
    /// activation-time "now" seed.
    /// </summary>
    public async Task RewindAsync(OctoObjectId rollupRtId, DateTime toBucketEnd) =>
        await (await TenantAsync()).GetRollupOrchestrator()!.RewindWatermarkAsync(rollupRtId, toBucketEnd);

    /// <summary>
    /// Runs a manual recompute of <c>[from, to)</c>. This is how a historic ladder is populated:
    /// the forward tick only walks from the activation watermark and is capped per pass, whereas a
    /// recompute processes exactly the requested range — through the same per-bucket aggregation
    /// SQL and the same per-source segmentation.
    /// </summary>
    public async Task<RecomputeJobSnapshot> RecomputeAsync(
        OctoObjectId rollupRtId, DateTime from, DateTime to, OctoObjectId? rtIdScope = null)
    {
        var orchestrator = (await TenantAsync()).GetRecomputeOrchestrator()!;
        var job = await orchestrator.RecomputeArchiveAsync(
            rollupRtId, from, to, rtIdScope, RecomputeTrigger.Manual, CancellationToken.None);
        await RefreshAsync(rollupRtId);

        // The read path picks the active generation out of the generation map, so a query issued
        // before CrateDB has applied the pointer flip to the read path sees NO active generation and
        // returns nothing. Refresh the side table too, not just the archive.
        await ExecuteSqlAsync($"REFRESH TABLE {GenMapTable(rollupRtId)}");
        return job;
    }

    /// <summary>The rollup's Mongo snapshot (sources, watermark, status, freeze).</summary>
    public async Task<RollupArchiveSnapshot> LoadRollupAsync(OctoObjectId rollupRtId) =>
        (await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rollupRtId))!;

    /// <summary>CrateDB applies inserts asynchronously to the read path; force a refresh.</summary>
    public async Task RefreshAsync(OctoObjectId archiveRtId) =>
        await ExecuteSqlAsync($"REFRESH TABLE {QualifiedTable(archiveRtId)}");

    /// <summary>Fully-qualified CrateDB table of an archive.</summary>
    public string QualifiedTable(OctoObjectId archiveRtId) =>
        $"\"{fixture.StreamDataTenantId}\".\"archive_{archiveRtId}\"";

    /// <summary>Fully-qualified generation-map side table of a rollup archive.</summary>
    public string GenMapTable(OctoObjectId rollupRtId) =>
        $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"";

    /// <summary>
    /// Every materialised bucket of a rollup as <c>window_start → value</c>, ordered in time.
    /// After a completed recompute the post-flip sweep has already removed every superseded
    /// generation in the recomputed range, so the live table carries exactly one row per
    /// (bucket, series) — the same rows the read path serves.
    /// </summary>
    public async Task<IReadOnlyList<(DateTime WindowStart, double? Value)>> ReadBucketsAsync(
        OctoObjectId rollupRtId, string column = RollupColumn, OctoObjectId? seriesRtId = null)
    {
        // The forward tick and the recompute both write asynchronously to the read path.
        await RefreshAsync(rollupRtId);

        var scope = seriesRtId is null ? string.Empty : $" WHERE \"rtid\" = '{seriesRtId}'";
        var sql =
            $"SELECT \"window_start\"::bigint AS ws, \"{column}\" AS v FROM {QualifiedTable(rollupRtId)}" +
            $"{scope} ORDER BY ws";

        var buckets = new List<(DateTime, double?)>();
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var windowStart = DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)).UtcDateTime;
            var value = reader.IsDBNull(1)
                ? (double?)null
                : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
            buckets.Add((windowStart, value));
        }

        return buckets;
    }

    /// <summary>
    /// Reads a rollup rung through the DOWNSAMPLING path (the line-chart path) targeting
    /// <paramref name="targetPoints"/> bins over <c>[from, to)</c>, returning the bins ascending with
    /// the aggregation value (null for an empty bin). Unlike <see cref="ReadBucketsAsync"/> — which
    /// reads the stored table directly — this exercises <c>CrateDbStreamDataRepository</c>'s bin
    /// geometry, so it is what proves a calendar-aligned rung downsamples onto its own calendar
    /// windows instead of an empty chart (AB#5157 review).
    /// </summary>
    public async Task<IReadOnlyList<(DateTime Timestamp, double? Value)>> DownsampleAsync(
        OctoObjectId rollupRtId, DateTime from, DateTime to, int targetPoints,
        string sourcePath = VoltagePath, string column = RollupColumn, OctoObjectId? seriesRtId = null)
    {
        await RefreshAsync(rollupRtId);
        var repo = (await TenantAsync()).GetStreamDataRepository()!;
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(new RtCkId<CkTypeId>(fixture.TestCkTypeId))
            .WithAggregationColumns([new AggregationColumn(sourcePath, AggregationFunction.Sum)])
            .WithTimeRange(from, to)
            .WithLimit(targetPoints);
        if (seriesRtId is not null)
        {
            options = options.WithRtIds([seriesRtId.Value]);
        }

        var result = await repo.ExecuteDownsamplingQueryAsync(rollupRtId, options);
        return result.Rows
            .Select(r => (
                r.Timestamp!.Value,
                r.Values.TryGetValue(column, out var v) && v is not null
                    ? Convert.ToDouble(v, CultureInfo.InvariantCulture)
                    : (double?)null))
            .OrderBy(r => r.Item1)
            .ToList();
    }

    /// <summary>Executes one statement against CrateDB (DDL / DML / REFRESH).</summary>
    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Single scalar as <see langword="long"/> (counts, generations, epoch-ms columns).</summary>
    public async Task<long> ScalarLongAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>All rows of a query as string-keyed dictionaries (small result sets only).</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(string sql)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }
}
