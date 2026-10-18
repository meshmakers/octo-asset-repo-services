using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.CrateVersions;

/// <summary>
///     The helpers of the CrateDB version matrix (AB#5490): provision an archive on a fixture, dump it to the S3 store on one
///     CrateDB version and load it on another, and compare what the two tables hold with the column aggregates the story asks
///     for (row count, and per column sum, min, max and count of non-null values).
/// </summary>
internal static class CrateVersionMatrixKit
{
    /// <summary>The platform's repository of the fixture's system tenant.</summary>
    public static async Task<IStreamDataRepository> RepositoryAsync(StreamDataFixture fixture)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        return tenantContext.GetStreamDataRepository()
               ?? throw new InvalidOperationException("The stream data repository is not available.");
    }

    /// <summary>Creates a raw archive on <paramref name="ckTypeId" /> with the given column paths and activates it.</summary>
    public static async Task<OctoObjectId> CreateRawArchiveAsync(
        StreamDataFixture fixture, string wellKnownName, string ckTypeId, IReadOnlyList<string> columnPaths)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        foreach (var path in columnPaths)
        {
            columns.Add(new RtCkArchiveColumnRecord { Path = path, Indexed = true, Required = false });
        }

        var archive = new RtRawArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = ckTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = columns
        };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var lifecycle = tenantContext.GetArchiveLifecycleService()
                        ?? throw new InvalidOperationException("The archive lifecycle service is not registered.");
        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    public static string Table(StreamDataFixture fixture, OctoObjectId archiveRtId) =>
        $"\"{fixture.StreamDataTenantId}\".\"archive_{archiveRtId}\"";

    public static async Task RefreshAsync(StreamDataFixture fixture, OctoObjectId archiveRtId) =>
        await ExecuteAsync(fixture, $"REFRESH TABLE {Table(fixture, archiveRtId)}");

    public static async Task ExecuteAsync(StreamDataFixture fixture, string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Dumps the archive to the S3 store on the source CrateDB and loads it into the target archive on the target CrateDB.</summary>
    public static async Task<(ArchiveCopyDumpResult Dump, ArchiveCopyLoadResult Load)> DumpAndLoadAsync(
        StreamDataFixture source, OctoObjectId sourceArchive, StreamDataFixture target, OctoObjectId targetArchive,
        CancellationToken ct)
    {
        var options = new ArchiveCopyOptions();
        var location = CrateVersionMatrixInfrastructure.Location($"matrix/{Guid.NewGuid():N}");

        var sourceRepository = await RepositoryAsync(source);
        var dump = await sourceRepository.DumpArchiveToStagingAsync(sourceArchive, location, options, ct);
        if (!dump.IsSuccess)
        {
            throw new InvalidOperationException("The dump failed: " + string.Join(" | ",
                dump.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));
        }

        var targetRepository = await RepositoryAsync(target);
        var load = await targetRepository.LoadArchiveFromStagingAsync(targetArchive, dump.Plan, location, options, ct);
        if (!load.IsSuccess)
        {
            throw new InvalidOperationException("The load failed: " + string.Join(" | ",
                load.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));
        }

        await RefreshAsync(target, targetArchive);
        return (dump, load);
    }

    /// <summary>
    ///     Row count and the aggregates of every column of the archive table, as text keyed by <c>column.aggregate</c>.
    ///     Numeric columns: sum, min, max, count; timestamp columns: min, max, count (as epoch milliseconds); boolean columns:
    ///     count and count of true; text columns: count, min and max length; other columns (geo point, objects, arrays): count of
    ///     non-null values. Columns whose value legitimately differs by restore time are listed in <paramref name="skipColumns" />.
    /// </summary>
    public static async Task<SortedDictionary<string, string>> AggregatesAsync(
        StreamDataFixture fixture, OctoObjectId archiveRtId, IReadOnlySet<string>? skipColumns = null)
    {
        var schema = fixture.StreamDataTenantId;
        var tableName = $"archive_{archiveRtId}";
        var columns = new List<(string Name, string Type)>();
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT column_name, data_type FROM information_schema.columns " +
                         "WHERE table_schema = @s AND table_name = @t AND column_name NOT LIKE '%[%' ORDER BY ordinal_position",
                         conn))
        {
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("t", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (skipColumns is null || !skipColumns.Contains(name))
                {
                    columns.Add((name, reader.GetString(1)));
                }
            }
        }

        var select = new StringBuilder("SELECT COUNT(*)");
        var labels = new List<string> { "rows" };
        foreach (var (name, type) in columns)
        {
            var q = $"\"{name.Replace("\"", "\"\"")}\"";
            switch (type)
            {
                case "double precision" or "real":
                    // A floating point sum depends on the order of the additions, which differs with the shard layout;
                    // rounding to 1e-6 keeps the comparison exact for real data loss and tolerant to the last ulp.
                    Add($"CAST(ROUND(SUM(CAST({q} AS DOUBLE PRECISION)) * 1000000) AS TEXT)", "sum");
                    Add($"CAST(MIN({q}) AS TEXT)", "min");
                    Add($"CAST(MAX({q}) AS TEXT)", "max");
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    break;
                case "integer" or "bigint" or "smallint":
                    Add($"CAST(SUM({q}) AS TEXT)", "sum");
                    Add($"CAST(MIN({q}) AS TEXT)", "min");
                    Add($"CAST(MAX({q}) AS TEXT)", "max");
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    break;
                case "timestamp with time zone" or "timestamp without time zone":
                    Add($"CAST(MIN({q}) AS TEXT)", "min");
                    Add($"CAST(MAX({q}) AS TEXT)", "max");
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    break;
                case "boolean":
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    Add($"CAST(COUNT(CASE WHEN {q} THEN 1 END) AS TEXT)", "true");
                    break;
                case "text" or "character varying":
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    Add($"CAST(MIN(LENGTH({q})) AS TEXT)", "minLength");
                    Add($"CAST(MAX(LENGTH({q})) AS TEXT)", "maxLength");
                    break;
                default:
                    Add($"CAST(COUNT({q}) AS TEXT)", "count");
                    break;
            }

            void Add(string expression, string label)
            {
                select.Append(", ").Append(expression);
                labels.Add($"{name}.{label}");
            }
        }

        select.Append(" FROM \"").Append(schema).Append("\".\"").Append(tableName).Append('"');
        await using var query = new NpgsqlCommand(select.ToString(), conn);
        await using var result = await query.ExecuteReaderAsync();
        await result.ReadAsync();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < labels.Count; i++)
        {
            values[labels[i]] = result.IsDBNull(i) ? "NULL" : Convert.ToString(result.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL";
        }

        return values;
    }

    /// <summary>The bytes the primary shards of the archive table occupy on disk, after a forced merge.</summary>
    public static async Task<long> TableBytesAsync(StreamDataFixture fixture, OctoObjectId archiveRtId)
    {
        await ExecuteAsync(fixture, $"OPTIMIZE TABLE {Table(fixture, archiveRtId)}");
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(size), 0) FROM sys.shards WHERE schema_name = @s AND table_name = @t AND \"primary\" = TRUE",
            conn);
        cmd.Parameters.AddWithValue("s", fixture.StreamDataTenantId);
        cmd.Parameters.AddWithValue("t", $"archive_{archiveRtId}");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Rows of the matrix archives: <c>Voltage</c> is NULL in every 7th row and <c>Current</c> in every 5th, so the dump
    ///     carries NULLs in columns without a DEFAULT. Timestamps run in 10-second steps from <see cref="SeedStart" />.
    /// </summary>
    public static async Task SeedAsync(StreamDataFixture fixture, OctoObjectId archiveRtId, int rows)
    {
        var repository = await RepositoryAsync(fixture);
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);
        const int batchSize = 1000;
        for (var offset = 0; offset < rows; offset += batchSize)
        {
            var points = new List<StreamDataPoint>(batchSize);
            for (var i = offset; i < Math.Min(rows, offset + batchSize); i++)
            {
                points.Add(new StreamDataPoint
                {
                    RtId = OctoObjectId.GenerateNewId(),
                    CkTypeId = ckTypeId,
                    Timestamp = SeedStart.AddSeconds(i * 10L),
                    RtWellKnownName = $"MatrixPoint{i:D6}",
                    Attributes = new Dictionary<string, object?>
                    {
                        ["voltage"] = i % 7 == 0 ? null : 220.0 + (i % 1000) * 0.25,
                        ["current"] = i % 5 == 0 ? null : 10.0 + (i % 100) * 0.1,
                        ["operatingStatus"] = i % 3
                    }
                });
            }

            await repository.InsertAsync(archiveRtId, points);
        }

        await RefreshAsync(fixture, archiveRtId);
    }

    public static readonly DateTime SeedStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The environment variable that sets the row count of the matrix archives (default <see cref="DefaultRows" />).</summary>
    public const string RowsVariable = "OCTO_CRATE_MATRIX_ROWS";

    public const int DefaultRows = 20_000;

    public static int Rows() =>
        int.TryParse(Environment.GetEnvironmentVariable(RowsVariable), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var rows) && rows > 0
            ? rows
            : DefaultRows;

    /// <summary>
    ///     Writes the rows of the archive as a format 1 backup: one JSON object per line, written the way the bot's tenant dump
    ///     writes them (<c>JsonSerializerDefaults.Web</c>, the rows of <c>ExportRowsAsync</c>).
    /// </summary>
    public static async Task<long> WriteFormat1Async(
        StreamDataFixture source, OctoObjectId archiveRtId, string path, CancellationToken ct)
    {
        var repository = await RepositoryAsync(source);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        long rows = 0;
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024) { NewLine = "\n" };
        await foreach (var row in repository.ExportRowsAsync(archiveRtId, null, ct))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, options).AsMemory(), ct);
            rows++;
        }

        return rows;
    }

    /// <summary>
    ///     Reads a format 1 backup into rows with CLR values: the reading code of the bot's archive import
    ///     (<c>NdjsonRowReader</c> in octo-bot-services), kept in step by hand because the bot is another repository.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadFormat1Async(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var timestampColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "timestamp", "window_start", "window_end", "rtcreationdatetime", "rtchangeddatetime"
        };
        using var reader = new StreamReader(path, Encoding.UTF8, true, 64 * 1024);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, options)!;
            var row = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
            foreach (var (column, element) in raw)
            {
                row[column] = element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String when timestampColumns.Contains(column) &&
                                              element.TryGetDateTimeOffset(out var dto) => dto.UtcDateTime,
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (object)element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => element.GetRawText()
                };
            }

            yield return row;
        }
    }

    /// <summary>The first row of a query as text values (NULL as "NULL").</summary>
    public static async Task<List<string>> RowAsync(StreamDataFixture fixture, string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var values = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values.Add(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL");
        }

        return values;
    }

    /// <summary>One scalar of the archive table of <paramref name="fixture" /> (as text).</summary>
    public static async Task<string> ScalarAsync(StreamDataFixture fixture, string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "NULL";
    }
}
