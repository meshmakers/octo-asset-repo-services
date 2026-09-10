using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5189 — the active-generation pointer is keyed on the exact range, so a recompute over a
/// different range adds an entry instead of replacing the previous one. Once the post-flip sweep has
/// removed the rows the old entry pointed at, that entry is dead weight: it names a generation that
/// no longer exists in the table, and left alone such entries accumulate for the life of the rollup
/// (observed live at three entries, one of them pointing at a swept-empty generation). The flip now
/// drops the entries its own range fully contains.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupRecomputeGenerationMapHygieneTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(15);
    private static readonly DateTime WideStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WideEnd = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InnerStart = new(2026, 1, 1, 10, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime InnerEnd = new(2026, 1, 1, 10, 45, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Recompute_OverAWiderRange_DropsThePointerEntriesItContains()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()!;
        var repo = tenantContext.GetStreamDataRepository()!;
        var executor = (IArchiveRecomputeExecutor)repo;

        var builder = new MultiSourceArchiveBuilder(fixture);
        var sourceRtId = await builder.CreateRawArchiveAsync("Ab5189GenMapSource");
        var seriesRtId = OctoObjectId.GenerateNewId();
        await builder.InsertPointsAsync(sourceRtId, seriesRtId,
        [
            (WideStart.AddMinutes(5), 10.0),
            (InnerStart.AddMinutes(5), 20.0),
            (InnerEnd.AddMinutes(5), 30.0),
        ]);

        var rollupRtId = await builder.CreateRollupAsync(
            "Ab5189GenMapRollup", [new RollupSourceReference(sourceRtId)], BucketSize);
        await builder.ActivateAsync(rollupRtId);

        var sourceSnapshot = await archiveStore.GetAsync(sourceRtId)!;
        var rollupSnapshot = await rollupStore.GetAsync(rollupRtId)!;
        var genMapTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"";

        // A narrow recompute first — this is the entry that later becomes redundant.
        await executor.ExecuteAsync(
            sourceSnapshot!, rollupSnapshot!, InnerStart, InnerEnd, null, CancellationToken.None);
        await RefreshAsync(genMapTable);
        (await CountPointersAsync(genMapTable)).Should().Be(1, "the narrow range flipped its own pointer");

        // Then a wider recompute that fully contains it.
        await executor.ExecuteAsync(
            sourceSnapshot!, rollupSnapshot!, WideStart, WideEnd, null, CancellationToken.None);
        await RefreshAsync(genMapTable);

        var pointers = await ReadPointersAsync(genMapTable);
        pointers.Should().HaveCount(1,
            "the contained entry points at a generation the wider flip's sweep has just emptied");
        pointers[0].RangeStartMs.Should().Be(new DateTimeOffset(WideStart).ToUnixTimeMilliseconds());
        pointers[0].RangeEndMs.Should().Be(new DateTimeOffset(WideEnd).ToUnixTimeMilliseconds());
        pointers[0].Generation.Should().Be(2);
    }

    [Fact]
    public async Task Recompute_OverANarrowerRange_KeepsTheEntryThatStillCoversTheRest()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()!;
        var repo = tenantContext.GetStreamDataRepository()!;
        var executor = (IArchiveRecomputeExecutor)repo;

        var builder = new MultiSourceArchiveBuilder(fixture);
        var sourceRtId = await builder.CreateRawArchiveAsync("Ab5189GenMapKeepSource");
        var seriesRtId = OctoObjectId.GenerateNewId();
        await builder.InsertPointsAsync(sourceRtId, seriesRtId,
        [
            (WideStart.AddMinutes(5), 10.0),
            (InnerStart.AddMinutes(5), 20.0),
        ]);

        var rollupRtId = await builder.CreateRollupAsync(
            "Ab5189GenMapKeepRollup", [new RollupSourceReference(sourceRtId)], BucketSize);
        await builder.ActivateAsync(rollupRtId);

        var sourceSnapshot = await archiveStore.GetAsync(sourceRtId)!;
        var rollupSnapshot = await rollupStore.GetAsync(rollupRtId)!;
        var genMapTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"";

        // Wide first, then narrow: the wide entry still governs the part outside the narrow range,
        // so dropping it would silently send that part back to generation 0.
        await executor.ExecuteAsync(
            sourceSnapshot!, rollupSnapshot!, WideStart, WideEnd, null, CancellationToken.None);
        await RefreshAsync(genMapTable);
        await executor.ExecuteAsync(
            sourceSnapshot!, rollupSnapshot!, InnerStart, InnerEnd, null, CancellationToken.None);
        await RefreshAsync(genMapTable);

        (await ReadPointersAsync(genMapTable)).Should().HaveCount(2,
            "an entry reaching beyond the flipped range must survive it");
    }

    private async Task<long> CountPointersAsync(string genMapTable) =>
        (await ReadPointersAsync(genMapTable)).Count;

    private async Task<IReadOnlyList<(long RangeStartMs, long RangeEndMs, long Generation)>> ReadPointersAsync(
        string genMapTable)
    {
        var rows = new List<(long, long, long)>();
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT \"range_start\"::bigint, \"range_end\"::bigint, \"generation\" FROM {genMapTable} " +
            "ORDER BY \"generation\" DESC", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return rows;
    }

    private async Task RefreshAsync(string qualifiedTable)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", connection);
        await command.ExecuteNonQueryAsync();
    }
}
