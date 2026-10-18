using System.Diagnostics;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Streams = Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.CrateVersions;

/// <summary>
///     AB#5490, the CrateDB 5.x to 6.x version matrix: an archive dumped on a 5.x CrateDB through the native copy
///     (<c>COPY … TO</c> into an S3 store, format 2) is restored on a 6.x CrateDB (<c>COPY … FROM</c>) and holds the same data.
///     Each pair of versions is one derived class on its own collection; the cases are written once here. The heavy tests carry
///     the trait <c>Category=CrateVersionMatrix</c>, so <c>dotnet test --filter "Category=CrateVersionMatrix"</c> runs them
///     separately and <c>--filter "Category!=CrateVersionMatrix"</c> leaves them out (see the README of the project).
/// </summary>
public abstract class CrateVersionMatrixCases(
    VersionedCrateStreamDataFixture source, VersionedCrateStreamDataFixture target, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Small data on the columns of the fixture's own archive: the smoke of the pair.</summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Smoke")]
    public async Task Smoke_ADumpOfTheFixtureArchive_RestoresWithIdenticalAggregates()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        output.WriteLine($"Source CrateDB {source.ServerVersion} ({source.Image}), target CrateDB {target.ServerVersion} ({target.Image})");

        var targetArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(target, "MatrixSmokeTarget",
            source.TestCkTypeId, ["Voltage", "Current", "OperatingStatus"]);

        var watch = Stopwatch.StartNew();
        var (dump, load) = await CrateVersionMatrixKit.DumpAndLoadAsync(source, source.ArchiveRtId, target, targetArchive, Ct);
        watch.Stop();
        output.WriteLine($"Dump attempts {dump.Attempts}, rows verified {load.RowCountVerified}, copy {watch.Elapsed.TotalSeconds:F1} s");

        var sourceAggregates = await CrateVersionMatrixKit.AggregatesAsync(source, source.ArchiveRtId);
        var targetAggregates = await CrateVersionMatrixKit.AggregatesAsync(target, targetArchive);
        foreach (var pair in sourceAggregates)
        {
            output.WriteLine($"  {pair.Key} = {pair.Value}");
        }

        sourceAggregates["rows"].Should().Be(source.TestDataPointCount.ToString());
        targetAggregates.Should().Equal(sourceAggregates);
    }

    private static readonly string[] ColumnPaths = ["Voltage", "Current", "OperatingStatus"];

    /// <summary>
    ///     Format 2: a seeded archive (NULLs in <c>voltage</c> and <c>current</c>) dumped on the source and loaded on the target
    ///     holds the same rows and the same column aggregates, and the sizes on disk are printed (AB#5490 criteria 1, 2, 6).
    /// </summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Full")]
    public async Task Format2_ASeededArchive_RestoresWithIdenticalRowsAndAggregates()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        var rows = CrateVersionMatrixKit.Rows();
        var sourceArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(source, "MatrixFormat2Source",
            source.TestCkTypeId, ColumnPaths);
        var targetArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(target, "MatrixFormat2Target",
            source.TestCkTypeId, ColumnPaths);
        await CrateVersionMatrixKit.SeedAsync(source, sourceArchive, rows);

        var watch = Stopwatch.StartNew();
        var (dump, load) = await CrateVersionMatrixKit.DumpAndLoadAsync(source, sourceArchive, target, targetArchive, Ct);
        watch.Stop();

        var sourceAggregates = await CrateVersionMatrixKit.AggregatesAsync(source, sourceArchive);
        var targetAggregates = await CrateVersionMatrixKit.AggregatesAsync(target, targetArchive);
        await ReportAsync("format 2", rows, watch.Elapsed, dump.Attempts, sourceArchive, targetArchive);

        sourceAggregates["rows"].Should().Be(rows.ToString());
        load.RowCountVerified.Should().BeTrue();
        targetAggregates.Should().Equal(sourceAggregates);
        // NULL in a column without a DEFAULT stays NULL: the same rows are empty on both sides.
        var nullVoltageSource = await CrateVersionMatrixKit.ScalarAsync(source,
            $"SELECT COUNT(*) FROM {CrateVersionMatrixKit.Table(source, sourceArchive)} WHERE \"voltage\" IS NULL");
        var nullVoltageTarget = await CrateVersionMatrixKit.ScalarAsync(target,
            $"SELECT COUNT(*) FROM {CrateVersionMatrixKit.Table(target, targetArchive)} WHERE \"voltage\" IS NULL");
        nullVoltageSource.Should().NotBe("0");
        nullVoltageTarget.Should().Be(nullVoltageSource);
    }

    /// <summary>
    ///     A NULL stored in a column that has a DEFAULT (<c>rtcreationdatetime</c> defaults to <c>CURRENT_TIMESTAMP</c>) is
    ///     restored by the native copy as the default, not as NULL (AB#5490 criterion 2, AB#6647).
    /// </summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Full")]
    public async Task Format2_ANullInAColumnWithADefault_IsRestoredWithTheDefault()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        var sourceArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(source, "MatrixDefaultSource",
            source.TestCkTypeId, ColumnPaths);
        var targetArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(target, "MatrixDefaultTarget",
            source.TestCkTypeId, ColumnPaths);
        await CrateVersionMatrixKit.SeedAsync(source, sourceArchive, 700);
        await CrateVersionMatrixKit.ExecuteAsync(source,
            $"UPDATE {CrateVersionMatrixKit.Table(source, sourceArchive)} SET \"rtcreationdatetime\" = NULL WHERE \"voltage\" IS NULL");
        await CrateVersionMatrixKit.RefreshAsync(source, sourceArchive);

        await CrateVersionMatrixKit.DumpAndLoadAsync(source, sourceArchive, target, targetArchive, Ct);

        var sourceNulls = await CrateVersionMatrixKit.ScalarAsync(source,
            $"SELECT COUNT(*) FROM {CrateVersionMatrixKit.Table(source, sourceArchive)} WHERE \"rtcreationdatetime\" IS NULL");
        var targetNulls = await CrateVersionMatrixKit.ScalarAsync(target,
            $"SELECT COUNT(*) FROM {CrateVersionMatrixKit.Table(target, targetArchive)} WHERE \"rtcreationdatetime\" IS NULL");
        output.WriteLine($"rtcreationdatetime IS NULL: source {sourceNulls}, target {targetNulls} of 700 rows");
        sourceNulls.Should().Be("100");
        // Documented behaviour (AB#5490, AB#6647): the copy applies the column's DEFAULT (CURRENT_TIMESTAMP) to a NULL, it
        // does not restore the NULL. The same on both series; a change on a CrateDB version fails here.
        targetNulls.Should().Be("0");
    }

    /// <summary>
    ///     Format 1: the rows of an archive of the source series, written as the bot's tenant dump writes them (NDJSON) and read
    ///     back the way the bot's import reads them, are imported through the row path of the target and give the same data
    ///     (AB#5490 criterion 4).
    /// </summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Full")]
    public async Task Format1_ARowBackupOfTheSourceSeries_RestoresThroughTheRowPath()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        var rows = CrateVersionMatrixKit.Rows();
        var sourceArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(source, "MatrixFormat1Source",
            source.TestCkTypeId, ColumnPaths);
        var targetArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(target, "MatrixFormat1Target",
            source.TestCkTypeId, ColumnPaths);
        // The control: the same import into a second archive of the SOURCE version, to tell what the row path does on every
        // version from what only the target version does.
        var controlArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(source, "MatrixFormat1Control",
            source.TestCkTypeId, ColumnPaths);
        await CrateVersionMatrixKit.SeedAsync(source, sourceArchive, rows);

        var backup = Path.Combine(Path.GetTempPath(), $"crate-matrix-{Guid.NewGuid():N}.ndjson");
        try
        {
            var watch = Stopwatch.StartNew();
            var written = await CrateVersionMatrixKit.WriteFormat1Async(source, sourceArchive, backup, Ct);
            var targetRepository = await CrateVersionMatrixKit.RepositoryAsync(target);
            await targetRepository.ImportRowsAsync(targetArchive,
                CrateVersionMatrixKit.ReadFormat1Async(backup, Ct), ArchiveImportMode.InsertOnly, Ct);
            await CrateVersionMatrixKit.RefreshAsync(target, targetArchive);
            watch.Stop();
            var sourceRepository = await CrateVersionMatrixKit.RepositoryAsync(source);
            await sourceRepository.ImportRowsAsync(controlArchive,
                CrateVersionMatrixKit.ReadFormat1Async(backup, Ct), ArchiveImportMode.InsertOnly, Ct);
            await CrateVersionMatrixKit.RefreshAsync(source, controlArchive);

            var sourceAggregates = await CrateVersionMatrixKit.AggregatesAsync(source, sourceArchive);
            var targetAggregates = await CrateVersionMatrixKit.AggregatesAsync(target, targetArchive);
            var controlAggregates = await CrateVersionMatrixKit.AggregatesAsync(source, controlArchive);
            await ReportAsync("format 1", rows, watch.Elapsed, 1, sourceArchive, targetArchive);
            output.WriteLine($"Backup file: {new FileInfo(backup).Length:N0} bytes, {written:N0} rows");

            written.Should().Be(rows);
            // The data of the rows is identical on the target version ...
            Data(targetAggregates).Should().Equal(Data(sourceAggregates));
            Data(controlAggregates).Should().Equal(Data(sourceAggregates));
            // ... and the row path treats the audit timestamps (rtcreationdatetime, rtchangeddatetime) on the target version
            // exactly as on the source version: preserved on both or re-stamped on both.
            foreach (var column in new[] { "rtcreationdatetime", "rtchangeddatetime" })
            {
                var preservedOnTarget = targetAggregates[$"{column}.max"] == sourceAggregates[$"{column}.max"];
                var preservedOnControl = controlAggregates[$"{column}.max"] == sourceAggregates[$"{column}.max"];
                output.WriteLine($"{column}: preserved by the row path on {target.ServerVersion}: {preservedOnTarget}, on {source.ServerVersion}: {preservedOnControl}");
                preservedOnTarget.Should().Be(preservedOnControl, $"{column} must not depend on the CrateDB version");
            }
        }
        finally
        {
            File.Delete(backup);
        }
    }

    /// <summary>
    ///     Every column type an archive supports (boolean, int, int64, enum, double, string, date-time, date-time-offset,
    ///     time-span, geo point, string array, int array, record, record array), with NULLs in every column: dumped on the source
    ///     and loaded on the target, the row count and the aggregates of every column are identical (AB#5490 criterion 1).
    /// </summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Full")]
    public async Task Format2_EveryColumnType_RestoresWithIdenticalAggregates()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        var rows = CrateVersionMatrixKit.Rows();
        const string probeType = "AssetRepositoryIntegrationTest/ColumnTypeProbe";
        var sourceArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(source, "MatrixProbeSource", probeType, ProbeColumns);
        var targetArchive = await CrateVersionMatrixKit.CreateRawArchiveAsync(target, "MatrixProbeTarget", probeType, ProbeColumns);

        // The geo point cannot be a CK attribute (the CK source generator does not compile it, see the probe type), so the
        // column is added to both tables by hand.
        foreach (var (fixture, archive) in new[] { (source, sourceArchive), (target, targetArchive) })
        {
            await CrateVersionMatrixKit.ExecuteAsync(fixture,
                $"ALTER TABLE {CrateVersionMatrixKit.Table(fixture, archive)} ADD COLUMN \"probegeopoint\" GEO_POINT");
        }

        await CrateVersionMatrixKit.ExecuteAsync(source, SeedSql(CrateVersionMatrixKit.Table(source, sourceArchive), rows));
        await CrateVersionMatrixKit.RefreshAsync(source, sourceArchive);

        var watch = Stopwatch.StartNew();
        var (dump, _) = await CrateVersionMatrixKit.DumpAndLoadAsync(source, sourceArchive, target, targetArchive, Ct);
        watch.Stop();

        var sourceAggregates = await CrateVersionMatrixKit.AggregatesAsync(source, sourceArchive);
        var targetAggregates = await CrateVersionMatrixKit.AggregatesAsync(target, targetArchive);
        await ReportAsync("format 2, every column type", rows, watch.Elapsed, dump.Attempts, sourceArchive, targetArchive);

        sourceAggregates["rows"].Should().Be(rows.ToString());
        // The aggregates cover every column; the columns the generic aggregates only count get their own below.
        targetAggregates.Should().Equal(sourceAggregates);
        foreach (var column in new[] { "probegeopoint", "proberecord", "proberecordarray", "probestringarray", "probeintarray" })
        {
            sourceAggregates[$"{column}.count"].Should().NotBe("0", column);
        }

        var details = (string table) =>
            "SELECT CAST(SUM(array_length(\"probestringarray\", 1)) AS TEXT), CAST(SUM(\"probeintarray\"[1]) AS TEXT), " +
            "CAST(SUM(array_length(\"proberecordarray\", 1)) AS TEXT), CAST(MIN(\"proberecord\"['Name']) AS TEXT), " +
            "CAST(MAX(\"proberecordarray\"[2]['Value']) AS TEXT), CAST(SUM(latitude(\"probegeopoint\")) AS TEXT), " +
            $"CAST(SUM(longitude(\"probegeopoint\")) AS TEXT) FROM {table}";
        var sourceDetails = await CrateVersionMatrixKit.RowAsync(source, details(CrateVersionMatrixKit.Table(source, sourceArchive)));
        var targetDetails = await CrateVersionMatrixKit.RowAsync(target, details(CrateVersionMatrixKit.Table(target, targetArchive)));
        output.WriteLine("array/record/geo details: " + string.Join(" | ", sourceDetails));
        targetDetails.Should().Equal(sourceDetails);
    }

    /// <summary>
    ///     <c>rows</c> rows with every probe column filled; every 9th row has NULL in the scalar columns and every 13th in the
    ///     array, record and geo columns.
    /// </summary>
    private static string SeedSql(string table, int rows) =>
        $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"rtwellknownname\", \"probeboolean\", \"probeint\", " +
        "\"probeint64\", \"probeenum\", \"probedouble\", \"probestring\", \"probedatetime\", \"probedatetimeoffset\", " +
        "\"probetimespan\", \"probegeopoint\", \"probestringarray\", \"probeintarray\", \"proberecord\", \"proberecordarray\") " +
        "SELECT substr(md5(CAST(generate_series AS TEXT)), 1, 24), " +
        "CAST(1767225600000 + CAST(generate_series AS BIGINT) * 1000 AS TIMESTAMP WITH TIME ZONE), " +
        "'AssetRepositoryIntegrationTest/ColumnTypeProbe', 'p' || generate_series, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE generate_series % 2 = 0 END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE generate_series END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE CAST(generate_series AS BIGINT) * 3000000000 + 1 END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE generate_series % 3 END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE generate_series * 0.25 END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE 'text ' || generate_series END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE CAST(1767225600000 + CAST(generate_series AS BIGINT) * 60000 AS TIMESTAMP WITH TIME ZONE) END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE CAST(1767229200000 + CAST(generate_series AS BIGINT) * 60000 AS TIMESTAMP WITH TIME ZONE) END, " +
        "CASE WHEN generate_series % 9 = 0 THEN NULL ELSE CAST(generate_series AS BIGINT) * 1000 END, " +
        "CASE WHEN generate_series % 13 = 0 THEN NULL ELSE CAST([8.0 + (generate_series % 100) * 0.01, 47.0 + (generate_series % 50) * 0.01] AS GEO_POINT) END, " +
        "CASE WHEN generate_series % 13 = 0 THEN NULL ELSE ['a' || generate_series, 'b'] END, " +
        "CASE WHEN generate_series % 13 = 0 THEN NULL ELSE [generate_series, generate_series + 1] END, " +
        "CASE WHEN generate_series % 13 = 0 THEN NULL ELSE {\"Name\" = 'n' || generate_series, \"Value\" = 'v', \"Unit\" = 'u'} END, " +
        "CASE WHEN generate_series % 13 = 0 THEN NULL ELSE [{\"Name\" = 'x', \"Value\" = '1', \"Unit\" = 'u'}, {\"Name\" = 'y', \"Value\" = 'v' || generate_series, \"Unit\" = 'u'}] END " +
        $"FROM generate_series(1, {rows})";

    private static readonly string[] ProbeColumns =
    [
        "ProbeBoolean", "ProbeInt", "ProbeInt64", "ProbeEnum", "ProbeDouble", "ProbeString", "ProbeDateTime",
        "ProbeDateTimeOffset", "ProbeTimeSpan", "ProbeStringArray", "ProbeIntArray", "ProbeRecord", "ProbeRecordArray"
    ];

    private static SortedDictionary<string, string> Data(IReadOnlyDictionary<string, string> aggregates) =>
        new(aggregates.Where(p => !p.Key.StartsWith("rtcreationdatetime.", StringComparison.Ordinal)
                                  && !p.Key.StartsWith("rtchangeddatetime.", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

    /// <summary>
    ///     A rollup whose range was recomputed twice (generation 1 live, generation 0 superseded) is dumped on the source and
    ///     loaded on the target: a reader sees the same values and the validator finds nothing (AB#5490 criterion 3).
    /// </summary>
    [Fact]
    [Trait("Category", "CrateVersionMatrix")]
    [Trait("Matrix", "Full")]
    public async Task Rollup_ARecomputedRange_ReadsTheSameValuesAfterTheRestore()
    {
        source.OutputHelper = output;
        target.OutputHelper = output;
        var sourceKit = await Streams.RollupTestKit.CreateAsync(source);
        var targetKit = await Streams.RollupTestKit.CreateAsync(target);
        var sourceRollup = await sourceKit.CreateRollupAsync("MatrixRollupSource");
        await sourceKit.RecomputeAsync(sourceRollup);
        await sourceKit.RecomputeAsync(sourceRollup);
        var before = await sourceKit.ReadActiveValuesAsync(sourceRollup);
        before.Should().NotBeEmpty();

        var targetRollup = await targetKit.CreateRollupAsync("MatrixRollupTarget");
        var options = new Meshmakers.Octo.Runtime.Contracts.StreamData.ArchiveCopyOptions();
        var location = CrateVersionMatrixInfrastructure.Location($"matrix/{Guid.NewGuid():N}");
        var dump = await sourceKit.Repository.DumpArchiveToStagingAsync(sourceRollup.RtId, location, options, Ct);
        dump.IsSuccess.Should().BeTrue(string.Join(" | ", dump.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));
        var load = await targetKit.Repository.LoadArchiveFromStagingAsync(targetRollup.RtId, dump.Plan, location, options, Ct);
        load.IsSuccess.Should().BeTrue(string.Join(" | ", load.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));
        await targetKit.RefreshAsync(targetRollup.Table);
        await targetKit.RefreshAsync(targetRollup.GenMapTable);

        var after = await targetKit.ReadActiveValuesAsync(targetRollup);
        output.WriteLine($"Rollup values before: {string.Join(", ", before)}; after: {string.Join(", ", after)}");
        after.Should().Equal(before);
        var report = await targetKit.Repository.ValidateRollupsAsync([targetRollup.RtId], Ct);
        report.IsOk.Should().BeTrue();
    }

    private async Task ReportAsync(
        string path, int rows, TimeSpan copy, int attempts, Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId sourceArchive,
        Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId targetArchive)
    {
        var sourceBytes = await CrateVersionMatrixKit.TableBytesAsync(source, sourceArchive);
        var targetBytes = await CrateVersionMatrixKit.TableBytesAsync(target, targetArchive);
        output.WriteLine(
            $"[{path}] {source.ServerVersion} -> {target.ServerVersion}: {rows:N0} rows, copy {copy.TotalSeconds:F1} s ({attempts} attempt(s)); " +
            $"disk {source.ServerVersion} {sourceBytes:N0} B ({(double)sourceBytes / rows:F1} B/row), " +
            $"{target.ServerVersion} {targetBytes:N0} B ({(double)targetBytes / rows:F1} B/row), " +
            $"disk factor {(double)targetBytes / Math.Max(1, sourceBytes):F2}");
    }
}

/// <summary>CrateDB 5.9.10 to 6.4.6.</summary>
[Collection(CrateMatrix59To6Collection.Name)]
public class CrateVersionMatrix59To6Tests(
    Crate59StreamDataFixture source, Crate6StreamDataFixture target, ITestOutputHelper output)
    : CrateVersionMatrixCases(source, target, output);

/// <summary>CrateDB 5.10.10 to 6.4.6.</summary>
[Collection(CrateMatrix510To6Collection.Name)]
public class CrateVersionMatrix510To6Tests(
    Crate510StreamDataFixture source, Crate6StreamDataFixture target, ITestOutputHelper output)
    : CrateVersionMatrixCases(source, target, output);
