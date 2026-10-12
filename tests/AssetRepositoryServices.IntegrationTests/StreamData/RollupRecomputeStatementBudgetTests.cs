using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#6283, against a REAL CrateDB and under a statement budget of
/// <see cref="StatementBudgetStreamDataFixture.StatementBudgetSeconds"/> second(s): a recompute
/// whose range holds far more rows than one statement can move inside the budget completes,
/// because no statement moves more than a slice; and a statement that does run out of its budget,
/// or is cancelled by its caller, says which of the two happened.
/// </summary>
[Collection(StreamDataStatementBudgetCollection.Name)]
public class RollupRecomputeStatementBudgetTests(StatementBudgetStreamDataFixture fixture, ITestOutputHelper output)
{
    private const int Series = 3000;
    private const int Buckets = 100;

    /// <summary>
    /// A statement that keeps CrateDB busy for many times the budget without holding rows in
    /// memory: an aggregate over a generated series.
    /// </summary>
    private const string SlowStatement =
        "SELECT sum(s.s::double precision) FROM generate_series(1, 2000000000) AS s(s)";

    private static readonly DateTime Anchor = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    /// <summary>
    /// 3,000 series over 100 hourly buckets — 300,000 rollup rows in one chunk — recomputed twice,
    /// so that the second run has a whole generation to sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured locally (single-node CrateDB 5.10.10 Testcontainer, 1 GB heap, 2026-10-12):
    /// </para>
    /// <para>
    /// Before AB#6283, same test and fixture: the first recompute fails after about 4 s with
    /// <c>XX000: Job killed. Cancellation request by: crate</c> — the copy of the 300,000 staged rows
    /// is one statement and is killed at the one-second budget; nothing is committed.
    /// </para>
    /// <para>
    /// With the row bound: both recomputes complete, in about 5 s and 12.5 s. The copy runs as 56
    /// statements and the sweep as 56 (slice target 5,000), each behind a count; the sweep's 56
    /// slices and their counts take about 7.5 s together, about 135 ms a slice — a seventh of the
    /// budget.
    /// </para>
    /// <para>
    /// A wall-clock test: the margin is what makes it trustworthy, and it is a property of the
    /// machine. It carries the trait <c>Category=StatementBudget</c> so it can be left out where
    /// the margin does not hold; the row bound itself is proven without a clock by
    /// <c>CrateDbArchiveRecomputeExecutorTests.LargeChunk_CopiesAndSweepsInRowBoundedSlices</c> in
    /// octo-construction-kit-engine-mongodb.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "StatementBudget")]
    public async Task ARecomputeOfALargeRange_StaysInsideTheStatementBudget()
    {
        fixture.OutputHelper = output;
        var source = await _builder.CreateRawArchiveAsync("BudgetSource");
        await SeedSourceAsync(source);

        var rollup = await _builder.CreateRollupAsync(
            "BudgetRollup", [new RollupSourceReference(source)], TimeSpan.FromHours(1));
        await _builder.ActivateAsync(rollup);
        var table = _builder.QualifiedTable(rollup);

        var watch = Stopwatch.StartNew();
        var first = await _builder.RecomputeAsync(rollup, Anchor, Anchor.AddHours(Buckets));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"first recompute: {first.State} in {watch.ElapsedMilliseconds} ms {first.ErrorReason}"));
        watch.Restart();
        var second = await _builder.RecomputeAsync(rollup, Anchor, Anchor.AddHours(Buckets));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"second recompute: {second.State} in {watch.ElapsedMilliseconds} ms {second.ErrorReason}"));

        first.State.Should().Be(RecomputeJobState.Completed, first.ErrorReason);
        second.State.Should().Be(RecomputeJobState.Completed, second.ErrorReason);
        await _builder.RefreshAsync(rollup);
        (await _builder.ScalarLongAsync($"SELECT count(*) FROM {table}")).Should().Be((long)Series * Buckets);
        (await _builder.ScalarLongAsync($"SELECT count(DISTINCT \"generation\") FROM {table}")).Should().Be(1,
            "the second run's sweep removed the whole first generation");
    }

    /// <summary>
    /// CrateDB answers every cancellation of a statement the same way — <c>XX000: Job killed</c> —
    /// whether the driver's command timeout or the caller's token asked for it. The client tells
    /// the two apart: an elapsed budget is a <see cref="StreamDataStatementTimeoutException"/>, a
    /// cancelled token an <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// Observed with Npgsql 10.0.3 against CrateDB 5.10.10: for a cancelled token the driver raises
    /// either its own <see cref="OperationCanceledException"/> ("Query was cancelled") or lets the
    /// server's <c>XX000</c> through as a <see cref="PostgresException"/> — both shapes occur for the
    /// same statement. The last block records which one a bare command got in this run and accepts
    /// both; the client has to turn both into a cancellation.
    /// </remarks>
    [Fact]
    [Trait("Category", "StatementBudget")]
    public async Task AStatementOverItsBudget_SurfacesAsStatementTimeout_AndACancelledTokenAsCancellation()
    {
        fixture.OutputHelper = output;
        var client = fixture.GetService<IStreamDataDatabaseClient>();
        var tenantId = fixture.StreamDataTenantId;
        var budget = TimeSpan.FromSeconds(StatementBudgetStreamDataFixture.StatementBudgetSeconds);

        // ── the budget runs out ──────────────────────────────────────────────────────────────────
        var watch = Stopwatch.StartNew();
        var timeout = (await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteNonQueryAsync(
            tenantId, SlowStatement, CrateStatementClass.Maintenance, CancellationToken.None)))
            .Should().BeOfType<StreamDataStatementTimeoutException>().Subject;
        watch.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"budget: {watch.ElapsedMilliseconds} ms, {timeout.Message}"));

        timeout.StatementClass.Should().Be(CrateStatementClass.Maintenance);
        timeout.Budget.Should().Be(budget);
        timeout.Elapsed.Should().BeGreaterThanOrEqualTo(budget);
        watch.Elapsed.Should().BeLessThan(budget + TimeSpan.FromSeconds(5),
            "the driver's command timeout ends the statement, ahead of the resilience pipeline's");
        var killed = timeout.InnerException.Should().BeOfType<PostgresException>().Subject;
        killed.SqlState.Should().Be("XX000");
        killed.MessageText.Should().Contain("Job killed");
        // Not a connection failure: the recompute orchestrator must not retry the chunk for it.
        foreach (var marker in new[] { "NpgsqlException", "reading from stream", "connection reset", "broken", "is unhealthy" })
        {
            timeout.Message.Should().NotContainEquivalentOf(marker);
        }

        // ── the caller cancels ───────────────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        watch.Restart();
        var cancelled = await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteNonQueryAsync(
            tenantId, SlowStatement, CrateStatementClass.Maintenance, cts.Token));
        watch.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cancel: {watch.ElapsedMilliseconds} ms, {cancelled.GetType().Name}: {cancelled.Message} / inner {cancelled.InnerException?.GetType().Name}"));

        cancelled.Should().BeAssignableTo<OperationCanceledException>();
        cancelled.Should().NotBeOfType<StreamDataStatementTimeoutException>();

        // ── what the driver itself raises for a cancelled token ──────────────────────────────────
        using var rawCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(SlowStatement, connection);
        var raw = await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync(rawCts.Token));
        output.WriteLine($"driver on a cancelled token: {raw.GetType().Name}: {raw.Message} / inner {raw.InnerException?.GetType().Name}");

        (raw is OperationCanceledException || raw is PostgresException { SqlState: "XX000" }).Should().BeTrue(
            "a cancelled statement surfaces as the driver's cancellation or as CrateDB's XX000, got {0}", raw.GetType());
    }

    /// <summary>
    /// One point per series and hour, written by server-side statements: 300,000 rows through the
    /// ingest path would take longer than the test itself.
    /// </summary>
    private async Task SeedSourceAsync(OctoObjectId source)
    {
        // One point through the repository tells the stored spelling of the CK type id.
        await _builder.InsertPointsAsync(source, OctoObjectId.GenerateNewId(), [(Anchor.AddHours(-48), 1d)]);
        var table = _builder.QualifiedTable(source);
        var ckTypeId = (string)(await _builder.RowsAsync($"SELECT \"cktypeid\" FROM {table} LIMIT 1"))[0]["cktypeid"]!;
        var anchorMs = new DateTimeOffset(Anchor).ToUnixTimeMilliseconds();

        // One statement per hour: a cross join of both series would be a 300,000 row nested loop,
        // which the test container's query memory limit refuses.
        for (var hour = 0; hour < Buckets; hour++)
        {
            var timestampMs = anchorMs + hour * 3_600_000L + 600_000L;
            await _builder.ExecuteSqlAsync(
                $"INSERT INTO {table} (\"rtid\", \"cktypeid\", \"timestamp\", \"rtwellknownname\", \"voltage\") " +
                $"SELECT lpad(s.s::text, 24, '0'), '{ckTypeId.Replace("'", "''")}', " +
                $"{timestampMs.ToString(CultureInfo.InvariantCulture)}, 'seeded', 1.0 " +
                $"FROM generate_series(1, {Series.ToString(CultureInfo.InvariantCulture)}) AS s(s)");
        }

        await _builder.RefreshAsync(source);
    }
}
