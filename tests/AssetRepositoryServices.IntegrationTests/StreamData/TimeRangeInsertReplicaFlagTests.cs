using System.Globalization;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#6647, against a REAL CrateDB cluster with a replica copy of every table: the
/// <c>was_updated</c> flag of a windowed row is never NULL, on either copy of the shard.
/// <para>
/// CrateDB before 5.10.12 stores the NEW rows of a multi-row
/// <c>INSERT … ON CONFLICT DO UPDATE</c> that also hits an existing key in the same shard without
/// their deterministic column defaults — on the replica copy only. A flag left to
/// <c>DEFAULT FALSE</c> is then missing there, reads as NULL although the column is NOT NULL, and a
/// rollup over it loses the row. The engine therefore writes the flag explicitly.
/// </para>
/// <para>
/// What makes these tests able to fail: the table has an assigned replica (asserted before the
/// write — on a single node they would pass with and without the fix), the statement under test
/// mixes existing and new keys of one shard, and the result is read with <c>GROUP BY</c> many
/// times on every node, because a read is served by the primary or the replica copy and
/// <c>IS NULL</c> on a NOT NULL column finds nothing.
/// </para>
/// </summary>
[Collection(StreamDataReplicatedCollection.Name)]
public class TimeRangeInsertReplicaFlagTests(ReplicatedStreamDataFixture fixture, ITestOutputHelper output)
{
    private const int Windows = 10;

    /// <summary>Reads per node; with two copies, 15 reads on each of two nodes miss one copy with 2^-29.</summary>
    private const int ReadsPerNode = 15;

    private static readonly DateTime Anchor = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WindowSize = TimeSpan.FromMinutes(15);

    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    [Fact]
    public async Task AStatementMixingExistingAndNewWindows_StoresTheFlagOnBothShardCopies()
    {
        fixture.OutputHelper = output;
        output.WriteLine(
            $"CrateDB cluster: {CrateDbTestCluster.NodeCount} nodes, formed in " +
            $"{fixture.ClusterStartupDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
        var archive = await _builder.CreateTimeRangeArchiveAsync("ReplicaFlagPlain", WindowSize);
        await AssertReplicaAssignedAsync(archive);
        var series = OctoObjectId.GenerateNewId();

        await _builder.InsertWindowsAsync(archive, series, WindowRows(0, Windows, value: 1));
        // ONE statement: the ten stored windows again (conflict, flag flips to TRUE) and ten new ones.
        await _builder.InsertWindowsAsync(archive, series, WindowRows(0, 2 * Windows, value: 2));

        await AssertFlagGroupsOnEveryCopyAsync(archive, $"FALSE={Windows}, TRUE={Windows}");
    }

    [Fact]
    public async Task WithConflictPrecedence_AcceptedRejectedAndNewWindows_StoreTheFlagOnBothShardCopies()
    {
        fixture.OutputHelper = output;
        var archive = await CreateTimeRangeArchiveWithPrecedenceAsync("ReplicaFlagGuarded");
        await AssertReplicaAssignedAsync(archive);
        var series = OctoObjectId.GenerateNewId();

        await _builder.InsertWindowsAsync(archive, series, WindowRows(0, 2 * Windows, value: 5));
        // ONE statement: ten windows the guard accepts (higher voltage wins), ten it rejects, ten new.
        await _builder.InsertWindowsAsync(archive, series,
            WindowRows(0, Windows, value: 9)
                .Concat(WindowRows(Windows, Windows, value: 1))
                .Concat(WindowRows(2 * Windows, Windows, value: 5)));

        // Rejected and new windows are unflagged, accepted ones flagged.
        await AssertFlagGroupsOnEveryCopyAsync(archive, $"FALSE={2 * Windows}, TRUE={Windows}");

        // A rejected write onto every row must leave each flag as it is, and no NULL behind.
        await _builder.InsertWindowsAsync(archive, series, WindowRows(0, 3 * Windows, value: 0));
        await AssertFlagGroupsOnEveryCopyAsync(archive, $"FALSE={2 * Windows}, TRUE={Windows}");
    }

    [Fact]
    public async Task ARollupBucketAggregatedAgainWithANewSeries_StoresTheFlagOnBothShardCopies()
    {
        fixture.OutputHelper = output;
        var source = await _builder.CreateRawArchiveAsync("ReplicaFlagRollupSource");
        var rollup = await _builder.CreateRollupAsync(
            "ReplicaFlagRollup", [new RollupSourceReference(source)], TimeSpan.FromHours(1));
        await _builder.ActivateAsync(rollup);
        await AssertReplicaAssignedAsync(rollup);

        var existing = Enumerable.Range(0, Windows).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        foreach (var rtId in existing)
        {
            await _builder.InsertPointsAsync(source, rtId, [(Anchor.AddMinutes(10), 1d)]);
        }

        await _builder.RewindAsync(rollup, Anchor);
        (await _builder.TickAsync(rollup)).Should().BeGreaterThan(0);

        // The same bucket again, now with ten more series: the aggregation statement upserts the ten
        // stored rows (flag flips to TRUE) and inserts ten new ones. The source is a raw archive, so
        // the flag of a new rollup row comes from the statement itself, not from a source flag.
        foreach (var rtId in Enumerable.Range(0, Windows).Select(_ => OctoObjectId.GenerateNewId()))
        {
            await _builder.InsertPointsAsync(source, rtId, [(Anchor.AddMinutes(20), 1d)]);
        }

        await _builder.RewindAsync(rollup, Anchor);
        (await _builder.TickAsync(rollup)).Should().BeGreaterThan(0);

        await AssertFlagGroupsOnEveryCopyAsync(rollup, $"FALSE={Windows}, TRUE={Windows}");
    }

    private IEnumerable<(DateTime From, DateTime To, double Value)> WindowRows(int first, int count, double value) =>
        Enumerable.Range(first, count).Select(k =>
            (Anchor.Add(WindowSize * k), Anchor.Add(WindowSize * (k + 1)), value));

    /// <summary>
    /// A time-range archive whose conflict precedence is <c>Voltage, HigherWins</c>: a rewrite with
    /// a higher voltage is accepted, one with a lower voltage rejected.
    /// </summary>
    private async Task<OctoObjectId> CreateTimeRangeArchiveWithPrecedenceAsync(string namePrefix)
    {
        var archive = new RtTimeRangeArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Period = WindowSize,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = MultiSourceArchiveBuilder.VoltagePath, Indexed = true, Required = false }
            },
            ConflictPrecedence = new AttributeRecordValueList<RtCkArchiveConflictKeyRecord>
            {
                new() { Column = MultiSourceArchiveBuilder.VoltagePath, Order = RtCkConflictKeyOrderEnum.HigherWins }
            }
        };

        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await _builder.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    /// <summary>
    /// The precondition without which nothing here can fail: the table's one shard has a started
    /// primary AND a started replica. A replica that is still being built when the statement runs
    /// is copied from the primary afterwards and comes out right.
    /// </summary>
    private async Task AssertReplicaAssignedAsync(OctoObjectId archiveRtId)
    {
        var sql =
            "SELECT COUNT(*) FROM sys.shards " +
            $"WHERE schema_name = '{fixture.StreamDataTenantId}' AND table_name = 'archive_{archiveRtId}' " +
            "AND state = 'STARTED'";

        long started = 0;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            started = await _builder.ScalarLongAsync(sql);
            if (started == 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        started.Should().Be(2, "the archive table needs a started primary and a started replica copy");
    }

    /// <summary>
    /// Reads <c>was_updated, COUNT(*) … GROUP BY was_updated</c> <see cref="ReadsPerNode"/> times on
    /// every node and requires every single answer to be <paramref name="expected"/>. A copy that
    /// lacks the flag shows as a second answer with a <c>NULL</c> group.
    /// </summary>
    private async Task AssertFlagGroupsOnEveryCopyAsync(OctoObjectId archiveRtId, string expected)
    {
        var table = _builder.QualifiedTable(archiveRtId);
        await _builder.RefreshAsync(archiveRtId);

        var answers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var connectionString in fixture.NodeConnectionStrings)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            for (var read = 0; read < ReadsPerNode; read++)
            {
                var answer = await ReadFlagGroupsAsync(connection, table);
                answers[answer] = answers.GetValueOrDefault(answer) + 1;
            }
        }

        var observed = string.Join(" | ", answers.Select(a => $"{a.Value}x [{a.Key}]"));
        output.WriteLine($"{table}: {observed}");

        answers.Keys.Should().Equal([expected],
            $"every read on every shard copy must see the same flags and none of them NULL, but got: {observed}");
    }

    private static async Task<string> ReadFlagGroupsAsync(NpgsqlConnection connection, string table)
    {
        var groups = new List<string>();
        await using var command = new NpgsqlCommand(
            $"SELECT \"was_updated\", COUNT(*) FROM {table} GROUP BY \"was_updated\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var flag = reader.IsDBNull(0) ? "NULL" : reader.GetBoolean(0) ? "TRUE" : "FALSE";
            var count = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            groups.Add($"{flag}={count.ToString(CultureInfo.InvariantCulture)}");
        }

        groups.Sort(StringComparer.Ordinal);
        return string.Join(", ", groups);
    }
}
