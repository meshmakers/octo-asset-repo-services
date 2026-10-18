using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>Where a pointer flip (and a sweep) falls relative to the two reads of a dump (AB#6968, placement matrix).</summary>
internal enum Placement
{
    /// <summary>No recompute at all: the baseline.</summary>
    NoFlip,

    /// <summary>The flip is done before the dump starts.</summary>
    FlipBeforeDump,

    /// <summary>The flip falls after the generation map was copied and before the data is copied.</summary>
    FlipBetweenMapAndData,

    /// <summary>The flip falls after the last data slice was copied (before the repeat rule's second map read).</summary>
    FlipAfterData,

    /// <summary>The flip falls after the whole dump.</summary>
    FlipAfterDump,

    /// <summary>Flip and sweep both fall between the map copy and the data copy.</summary>
    FlipAndSweepBetweenMapAndData,

    /// <summary>Flip and sweep both fall after the last data slice.</summary>
    FlipAndSweepAfterData
}

/// <summary>One cell of the matrix: what the restore of a dump taken at that placement looks like.</summary>
internal sealed record MatrixCell(
    Placement Placement, bool GraceOn, bool RepeatOn, string Dump, int EmptyWindows, int DifferingWindows,
    int Orphans, int PointersWithoutRows, bool ValidatorOk, bool Passed)
{
    public string ToMarkdownRow() =>
        $"| {Placement} | {(GraceOn ? "on" : "off")} | {(RepeatOn ? "on" : "off")} | {Dump} | {EmptyWindows} | " +
        $"{DifferingWindows} | {Orphans} | {PointersWithoutRows} | {(ValidatorOk ? "ok" : "inconsistent")} | " +
        $"{(Passed ? "0 findings" : "FINDINGS")} |";
}

/// <summary>
///     The machinery of the placement matrix (AB#6968): a private raw archive and rollup, a disturbance that changes the source
///     and flips the pointer, a dump that places it at an exact moment, the restore into a fresh rollup and the three measures of
///     the pass criterion — empty windows, windows that differ from the source at the time of the map read, and orphans above
///     the pointer after the validator ran.
/// </summary>
internal sealed class PlacementMatrixKit
{
    private static readonly DateTime T0 = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int Windows = 6;
    private static readonly TimeSpan Bucket = TimeSpan.FromHours(1);
    private static readonly ConcurrentQueue<MatrixCell> Cells = new();

    private readonly StreamDataFixture _fixture;
    private readonly MultiSourceArchiveBuilder _builder;
    private readonly RollupTestKit _kit;
    private readonly OctoObjectId[] _series = [OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId()];
    private OctoObjectId _rawArchive;
    private int _lateWrites;

    private PlacementMatrixKit(StreamDataFixture fixture, RollupTestKit kit)
    {
        _fixture = fixture;
        _kit = kit;
        _builder = new MultiSourceArchiveBuilder(fixture);
    }

    public OctoObjectId RollupRtId { get; private set; }

    public RollupTestKit Kit => _kit;

    public static async Task<PlacementMatrixKit> CreateAsync(StreamDataFixture fixture) =>
        new(fixture, await RollupTestKit.CreateAsync(fixture));

    /// <summary>A raw archive with three series and six hourly windows, and a rollup recomputed over it (generation 1).</summary>
    public async Task<RollupHandle> SetUpAsync(string label)
    {
        _rawArchive = await _builder.CreateRawArchiveAsync($"Matrix{label}");
        foreach (var (series, index) in _series.Select((s, i) => (s, i)))
        {
            await _builder.InsertPointsAsync(_rawArchive, series,
                Enumerable.Range(0, Windows).Select(w => (T0.AddHours(w).AddMinutes(10), 1d + index + w)));
        }

        RollupRtId = await _builder.CreateRollupAsync($"MatrixRollup{label}",
            [new RollupSourceReference(_rawArchive)], Bucket);
        await _builder.ActivateAsync(RollupRtId);
        await _builder.RecomputeAsync(RollupRtId, T0, T0.AddHours(Windows));
        return await HandleAsync(RollupRtId);
    }

    /// <summary>
    ///     The disturbance: new points in every window of the first series change the source, then the range is recomputed — a
    ///     new generation, a pointer flip and (grace period zero) the immediate sweep of the previous generation.
    /// </summary>
    public async Task FlipAsync()
    {
        _lateWrites++;
        await _builder.InsertPointsAsync(_rawArchive, _series[0],
            Enumerable.Range(0, Windows).Select(w => (T0.AddHours(w).AddMinutes(20 + _lateWrites), 100d * _lateWrites)));
        await _builder.RecomputeAsync(RollupRtId, T0, T0.AddHours(Windows));
    }

    /// <summary>The sweep that is due: with a grace period it does nothing within the grace period.</summary>
    public Task<int> SweepAsync() => _kit.Repository.SweepSupersededGenerationsAsync(TestContext.Current.CancellationToken);

    /// <summary>A fresh, empty rollup of the same definition on the same raw archive: the target of a restore.</summary>
    public async Task<RollupHandle> CreateTargetAsync(string label)
    {
        var id = await _builder.CreateRollupAsync($"MatrixTarget{label}", [new RollupSourceReference(_rawArchive)], Bucket);
        await _builder.ActivateAsync(id);
        return await HandleAsync(id);
    }

    private async Task<RollupHandle> HandleAsync(OctoObjectId rollupRtId)
    {
        var tenant = await _builder.TenantAsync();
        var source = await tenant.GetArchiveRuntimeStore().GetAsync(_rawArchive)
                     ?? throw new InvalidOperationException("Source snapshot missing.");
        var rollup = await tenant.GetRollupArchiveRuntimeStore()!.GetAsync(rollupRtId)
                     ?? throw new InvalidOperationException("Rollup snapshot missing.");
        return new RollupHandle(rollupRtId, source, rollup, _builder.QualifiedTable(rollupRtId),
            _builder.GenMapTable(rollupRtId));
    }

    /// <summary>
    ///     What a reader of the rollup sees: per (window, series) the row of the generation the pointer selects (the highest
    ///     generation of a pointer entry that covers the window, 0 where none does).
    /// </summary>
    public async Task<Dictionary<(long Window, string Series), double?>> ReadActiveAsync(RollupHandle rollup)
    {
        await _kit.RefreshAsync(rollup.Table);
        await _kit.RefreshAsync(rollup.GenMapTable);

        await using var conn = new NpgsqlConnection(_fixture.CrateDbConnectionString);
        await conn.OpenAsync();

        var entries = new List<(long Start, long End, long Generation)>();
        await using (var cmd = new NpgsqlCommand(
                         $"SELECT \"range_start\", \"range_end\", \"generation\" FROM {rollup.GenMapTable}", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                entries.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)));
            }
        }

        var result = new Dictionary<(long, string), double?>();
        await using var rows = new NpgsqlCommand(
            $"SELECT \"window_start\"::bigint, \"rtid\", \"voltage_sum\", \"generation\" FROM {rollup.Table}", conn);
        await using var rowReader = await rows.ExecuteReaderAsync();
        while (await rowReader.ReadAsync())
        {
            var window = Convert.ToInt64(rowReader.GetValue(0), CultureInfo.InvariantCulture);
            var generation = Convert.ToInt64(rowReader.GetValue(3), CultureInfo.InvariantCulture);
            var active = entries.Where(e => window >= e.Start && window < e.End)
                .Select(e => e.Generation).DefaultIfEmpty(0).Max();
            if (generation == active)
            {
                result[(window, Convert.ToString(rowReader.GetValue(1), CultureInfo.InvariantCulture)!)] =
                    rowReader.IsDBNull(2) ? null : Convert.ToDouble(rowReader.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        return result;
    }

    /// <summary>Measures a restored rollup against the source as it was when the map was read.</summary>
    public async Task<MatrixCell> MeasureAsync(
        Placement placement, bool graceOn, bool repeatOn, string dump, RollupHandle restored,
        IReadOnlyDictionary<(long Window, string Series), double?> sourceAtMapRead)
    {
        var actual = await ReadActiveAsync(restored);
        var windows = sourceAtMapRead.Keys.Select(k => k.Window).Distinct().ToList();

        var empty = windows.Count(w => !actual.Keys.Any(k => k.Window == w));
        var differing = sourceAtMapRead.Count(kv =>
            !actual.TryGetValue(kv.Key, out var value) || !Nullable.Equals(value, kv.Value));

        var report = await _kit.Repository.ValidateRollupsAsync([restored.RtId], TestContext.Current.CancellationToken);
        var findings = report.Rollups.SelectMany(r => r.Findings).ToList();
        var orphans = findings.Count(f => f.Check == RollupConsistencyCheck.OrphansAbovePointer);
        var pointersWithoutRows = findings.Count(f => f.Check == RollupConsistencyCheck.PointerWithoutRows);

        var cell = new MatrixCell(placement, graceOn, repeatOn, dump, empty, differing, orphans, pointersWithoutRows,
            report.IsOk, empty == 0 && differing == 0 && orphans == 0 && pointersWithoutRows == 0);
        Record(cell);
        return cell;
    }

    private static void Record(MatrixCell cell)
    {
        Cells.Enqueue(cell);
        var table = new StringBuilder()
            .AppendLine("| Placement | Grace | Repeat | Dump | Empty windows | Windows differing from the source at map-read time | " +
                        "Orphans above pointer | Pointers without rows | Validator | Result |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var row in Cells.OrderBy(c => c.GraceOn).ThenBy(c => c.RepeatOn).ThenBy(c => c.Placement))
        {
            table.AppendLine(row.ToMarkdownRow());
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "placement-matrix-results.md"), table.ToString());
        }
        catch (IOException)
        {
            // The table is a by-product for the pull request; the assertions are the test.
        }
    }
}
