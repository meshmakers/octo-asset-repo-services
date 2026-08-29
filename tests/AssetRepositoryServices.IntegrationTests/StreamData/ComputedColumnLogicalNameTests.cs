using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4779 — a computed-column formula may be written in the archive's <b>logical</b> column
/// vocabulary (the CK attribute paths the Refinery Studio lists) instead of the physical CrateDB
/// names. The unit tests in the engine pin the rewrite itself; these pin the chain around it against
/// real CrateDB and MongoDB: the formula survives lifecycle validation, reaches the physical column,
/// and the backfill actually computes with it.
/// <para>
/// The fixture's archive exposes PascalCase paths (<c>Voltage</c>, <c>Current</c>) whose physical
/// columns are <c>voltage</c> / <c>current</c>. That is the case this suite can exercise end to end:
/// before AB#4779 the validator compared references against the physical set with
/// <c>StringComparer.Ordinal</c>, so <c>Voltage</c> was rejected outright. The dotted form
/// (<c>Amount.Value</c>) differs only inside the rewriter — covered by
/// <c>ComputedColumnFormulaRewriterTests</c> — because the test CK model has no numeric record
/// attribute to build a dotted archive column from (its only record values are strings, which a
/// formula may not reference at all).
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class ComputedColumnLogicalNameTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const double Factor = 2.0;

    [Fact]
    public async Task AddComputedColumn_WithLogicalColumnName_IsAcceptedAndComputes()
    {
        fixture.OutputHelper = output;
        const string columnName = "doubledVoltage";

        var (lifecycle, repo) = await ResolveAsync();
        var store = await ResolveStoreAsync();

        // "Voltage" is the CK attribute path — what the Studio shows. The physical column is
        // "voltage". Before AB#4779 this call threw ComputedColumnInvalidException.
        await lifecycle.AddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, $"Voltage * {Factor}", FormulaResultType.Double,
            indexed: false);

        try
        {
            await fixture.RefreshArchiveTableAsync(fixture.ArchiveRtIdString);

            var result = await repo.ExecuteQueryAsync(fixture.ArchiveRtId,
                StreamDataQueryOptions.Create()
                    .WithCkTypeId(new RtCkId<CkTypeId>(fixture.TestCkTypeId))
                    .WithColumns(["Voltage", columnName])
                    .WithTimeRange(fixture.TestDataStartTime, fixture.TestDataEndTime));

            result.Rows.Should().NotBeEmpty("the fixture seeded data points into this archive");

            // The storage key comes from the resolver, never from the name: deriving it here would
            // re-encode the rule AB#4764 centralised, and it would silently go wrong the moment the
            // column is versioned by a formula change.
            var storageKey = await ResolveStorageKeyAsync(store, columnName);

            foreach (var row in result.Rows)
            {
                var voltage = Convert.ToDouble(row.Values["voltage"]);
                row.Values[storageKey].Should().NotBeNull(
                    "a computed column whose formula was rewritten must still produce a value");
                Convert.ToDouble(row.Values[storageKey]).Should().BeApproximately(
                    voltage * Factor, 1e-9);
            }
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    [Fact]
    public async Task UpdateFormula_WithTheSameFormulaInLogicalSpelling_DoesNotVersionTheColumn()
    {
        fixture.OutputHelper = output;
        const string columnName = "trebledVoltage";

        var (lifecycle, _) = await ResolveAsync();
        var store = await ResolveStoreAsync();

        await lifecycle.AddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, "Voltage * 3", FormulaResultType.Double,
            indexed: false);

        try
        {
            var before = await ReadColumnAsync(store, columnName);
            before.ComputedVersion.Should().Be(0);
            before.Formula.Should().Be("voltage * 3", "the stored formula is the physical form");

            // Re-submitting the identical formula in the spelling the user typed must be recognised
            // as unchanged. Without the rewrite happening before the no-op check, this comparison
            // ("Voltage * 3" vs the stored "voltage * 3") reads as a change and the column is
            // versioned, backfilled and pointer-swapped — every single save from the UI.
            await lifecycle.UpdateComputedColumnFormulaAsync(
                fixture.ArchiveRtId, columnName, "Voltage * 3");

            var after = await ReadColumnAsync(store, columnName);
            after.ComputedVersion.Should().Be(before.ComputedVersion,
                "re-saving an unchanged formula must not version the column");
            after.ComputedState.Should().Be(ComputedColumnState.Active);
            // Not versioning is only half of "unchanged": storing the caller's spelling while leaving
            // the version alone would pass the check above and still break ingest on every new row,
            // exactly as the swap bug did.
            after.Formula.Should().Be("voltage * 3",
                "the stored formula must remain the physical form");
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    [Fact]
    public async Task ChangeFormula_ToALogicalSpelling_ComputesForRowsIngestedAfterwards()
    {
        // Field report from the Studio: after "Change formula" the panel showed the logical spelling
        // and the existing values still looked right — because the backfill had run off the pending
        // formula, which was rewritten. The committed Formula was not, and ingest evaluates that on
        // every new row: the column would have gone silently NULL from then on. Backfilled rows alone
        // cannot show this, so this test inserts a row *after* the change.
        fixture.OutputHelper = output;
        const string columnName = "changedVoltage";

        var (lifecycle, repo) = await ResolveAsync();
        var store = await ResolveStoreAsync();

        await lifecycle.AddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, "Voltage * 2", FormulaResultType.Double, indexed: false);

        try
        {
            await lifecycle.UpdateComputedColumnFormulaAsync(
                fixture.ArchiveRtId, columnName, "Voltage * 3");

            var column = await ReadColumnAsync(store, columnName);
            column.Formula.Should().Be("voltage * 3",
                "the committed formula must be the physical form, not the caller's spelling");
            column.ComputedVersion.Should().Be(1, "a real formula change versions the column");

            // A fresh row, ingested after the swap — this is the path that was broken.
            var timestamp = fixture.TestDataEndTime.AddHours(1);
            const double voltage = 400.0;
            await repo.InsertAsync(fixture.ArchiveRtId, new StreamDataPoint
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId),
                Timestamp = timestamp,
                RtWellKnownName = "PostChangeMeteringPoint",
                Attributes = new Dictionary<string, object?> { ["voltage"] = voltage }
            });
            await fixture.RefreshArchiveTableAsync(fixture.ArchiveRtIdString);

            var result = await repo.ExecuteQueryAsync(fixture.ArchiveRtId,
                StreamDataQueryOptions.Create()
                    .WithCkTypeId(new RtCkId<CkTypeId>(fixture.TestCkTypeId))
                    .WithColumns(["Voltage", columnName])
                    .WithTimeRange(timestamp.AddMinutes(-1), timestamp.AddMinutes(1)));

            var storageKey = await ResolveStorageKeyAsync(store, columnName);
            storageKey.Should().NotBe("changedvoltage",
                "a formula change moves the column to a versioned physical name");

            var row = result.Rows.Should().ContainSingle().Subject;
            row.Values[storageKey].Should().NotBeNull(
                "a row ingested after the formula change must still be computed");
            Convert.ToDouble(row.Values[storageKey]).Should().BeApproximately(voltage * 3, 1e-9);
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    [Fact]
    public async Task AddComputedColumn_WithPhysicalColumnName_StillWorks()
    {
        // Backwards compatibility: every formula stored before AB#4779 uses the physical form, and a
        // caller who prefers it must keep working — the rewriter leaves an unmatched run alone.
        fixture.OutputHelper = output;
        const string columnName = "halvedVoltage";

        var (lifecycle, _) = await ResolveAsync();
        var store = await ResolveStoreAsync();

        await lifecycle.AddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, "voltage / 2", FormulaResultType.Double,
            indexed: false);

        try
        {
            var column = await ReadColumnAsync(store, columnName);
            column.Formula.Should().Be("voltage / 2");
            column.ComputedState.Should().Be(ComputedColumnState.Active);
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    [Fact]
    public async Task AddComputedColumn_WithUnknownColumnName_IsRejectedByTheNameTheCallerWrote()
    {
        // The rewriter leaves an unknown run as written so the validator can name it back. A
        // half-rewritten formula would complain about a spelling nobody typed.
        fixture.OutputHelper = output;

        var (lifecycle, _) = await ResolveAsync();

        var act = async () => await lifecycle.AddComputedColumnAsync(
            fixture.ArchiveRtId, "bogus", "Voltaage * 2", FormulaResultType.Double, indexed: false);

        (await act.Should().ThrowAsync<ComputedColumnInvalidException>())
            .Which.Message.Should().Contain("Voltaage");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(IArchiveLifecycleService Lifecycle, IStreamDataRepository Repo)> ResolveAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        var lifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("Archive lifecycle service not available.");
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamDataRepository not available.");

        return (lifecycle, repo);
    }

    private async Task<IArchiveRuntimeStore> ResolveStoreAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        return tenantContext.GetArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Archive runtime store not available.");
    }

    /// <summary>
    /// The key a column's value arrives under in <c>StreamDataRow.Values</c>, asked of the storage
    /// layer's own resolver. Never derived from the column name: a computed column is lower-cased, and
    /// after a formula change it moves to a versioned physical name — the two reasons AB#4764 made this
    /// resolver the single source of truth for every reader.
    /// </summary>
    private async Task<string> ResolveStorageKeyAsync(IArchiveRuntimeStore store, string columnName)
    {
        var snapshot = await store.GetAsync(fixture.ArchiveRtId)
            ?? throw new InvalidOperationException("Archive snapshot not available.");

        return StreamDataFieldResolver.CreateForArchive(snapshot).Resolve(columnName)?.CrateDbName
               ?? throw new InvalidOperationException($"Column '{columnName}' does not resolve.");
    }

    private async Task<CkArchiveColumnSpec> ReadColumnAsync(IArchiveRuntimeStore store, string name)
    {
        var snapshot = await store.GetAsync(fixture.ArchiveRtId)
            ?? throw new InvalidOperationException("Archive snapshot not available.");

        return snapshot.Columns.FirstOrDefault(
                   c => c.IsComputed && string.Equals(c.Name, name, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"Computed column '{name}' not found.");
    }
}
