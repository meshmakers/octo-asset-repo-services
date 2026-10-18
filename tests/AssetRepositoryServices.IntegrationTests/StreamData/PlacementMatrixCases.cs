using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     The placement matrix of AB#6968: a rollup is dumped while a recompute flips its generation pointer (and sweeps the
///     previous generation) at an exact moment relative to the two reads of the dump, the dump is loaded into a fresh rollup, and
///     the restored rollup is measured — empty windows, windows that differ from the source at the time of the map read, orphans
///     above the pointer after the validator ran.
///     <para>
///         The cells with the grace period <b>on</b> and the repeat rule <b>on</b> are the pass criterion (0 / 0 / 0). The other
///         cells are characterisation tests: they record what happens without the grace period or without the repeat rule and
///         assert only that the dump and the load themselves ran; the observed result goes to the table
///         (<c>placement-matrix-results.md</c> next to the test assembly).
///     </para>
///     <para>
///         <b>Repeat rule off</b> is a dump driven slice by slice from the public copy contract (generation map first, then the
///         data slices, no second map read) with the disturbance placed between the steps; <b>repeat rule on</b> is the engine's
///         <c>DumpArchiveToStagingAsync</c>.
///     </para>
/// </summary>
public abstract class PlacementMatrixCases(StreamDataFixture fixture, ITestOutputHelper output, bool graceOn)
{
    private static readonly ArchiveCopyOptions Options = new();

    [Theory]
    [InlineData(Placement.NoFlip)]
    [InlineData(Placement.FlipBeforeDump)]
    [InlineData(Placement.FlipBetweenMapAndData)]
    [InlineData(Placement.FlipAfterData)]
    [InlineData(Placement.FlipAfterDump)]
    [InlineData(Placement.FlipAndSweepBetweenMapAndData)]
    [InlineData(Placement.FlipAndSweepAfterData)]
    internal async Task RepeatRuleOff_Characterisation(Placement placement)
    {
        fixture.OutputHelper = output;
        var cell = await RunAsync(placement, repeatOn: false);
        output.WriteLine(cell.ToMarkdownRow());
    }

    /// <summary>Runs one cell and returns its measures.</summary>
    private async Task<MatrixCell> RunAsync(Placement placement, bool repeatOn)
    {
        var ct = TestContext.Current.CancellationToken;
        var matrix = await PlacementMatrixKit.CreateAsync(fixture);
        var label = $"{placement}{(graceOn ? "G" : "N")}{(repeatOn ? "R" : "X")}";
        var source = await matrix.SetUpAsync(label);
        var target = await matrix.CreateTargetAsync(label);

        if (placement == Placement.FlipBeforeDump)
        {
            await matrix.FlipAsync();
        }

        var location = await RollupDumpRestoreSmokeTests.PrepareStagingAsync(matrix.Kit, source, Options, ct);

        IReadOnlyDictionary<(long Window, string Series), double?>? sourceAtMapRead = null;
        var disturbed = false;

        async Task OnMapReadAsync()
        {
            // The source as it is at the moment the map was read: the standard the restored rollup is measured against. A
            // repeated attempt reads the map again, so the last one counts.
            sourceAtMapRead = await matrix.ReadActiveAsync(source);
            if (!disturbed && placement is Placement.FlipBetweenMapAndData or Placement.FlipAndSweepBetweenMapAndData)
            {
                disturbed = true;
                await matrix.FlipAsync();
                if (placement == Placement.FlipAndSweepBetweenMapAndData)
                {
                    await matrix.SweepAsync();
                }
            }
        }

        async Task OnDataCopiedAsync()
        {
            if (!disturbed && placement is Placement.FlipAfterData or Placement.FlipAndSweepAfterData)
            {
                disturbed = true;
                await matrix.FlipAsync();
                if (placement == Placement.FlipAndSweepAfterData)
                {
                    await matrix.SweepAsync();
                }
            }
        }

        string dumpText;
        ArchiveCopyPlan loadPlan;
        if (repeatOn)
        {
            throw new NotSupportedException("The repeat rule runs inside the engine's dump; see RepeatRuleOn.");
        }

        (loadPlan, dumpText) = await DumpWithoutRepeatRuleAsync(matrix, source, location, OnMapReadAsync, OnDataCopiedAsync, ct);

        if (placement == Placement.FlipAfterDump)
        {
            await matrix.FlipAsync();
        }

        var load = await matrix.Kit.Repository.LoadArchiveFromStagingAsync(target.RtId, loadPlan, location, Options, ct);
        load.IsSuccess.Should().BeTrue("the load ran: " + string.Join(" | ", load.Slices.Select(s => $"{s.Slice.Key}: {s.Status} {s.Message}")));

        return await matrix.MeasureAsync(placement, graceOn, repeatOn, dumpText, target, sourceAtMapRead!);
    }

    /// <summary>
    ///     A dump without the repeat rule: the generation map slice first, then the data slices, nothing compared afterwards —
    ///     the engine's copy, driven from its public contract so the disturbance can be placed between the two steps.
    /// </summary>
    private static async Task<(ArchiveCopyPlan Plan, string Text)> DumpWithoutRepeatRuleAsync(
        PlacementMatrixKit matrix, RollupHandle source, ArchiveCopyStagingLocation location,
        Func<Task> afterMap, Func<Task> afterData, CancellationToken ct)
    {
        var repository = matrix.Kit.Repository;
        var plan = await repository.PlanArchiveCopyAsync(source.RtId, Options, ct);
        var mapSlices = plan.Slices.Where(s => s.Table == ArchiveCopyTable.GenerationMap).ToList();
        var dataSlices = plan.Slices.Where(s => s.Table != ArchiveCopyTable.GenerationMap).ToList();

        var results = new List<ArchiveCopySliceResult>();
        await foreach (var result in repository.CopyToStagingAsync(source.RtId, plan with { Slices = mapSlices },
                           location, Options, ct))
        {
            results.Add(result);
        }

        await afterMap();

        await foreach (var result in repository.CopyToStagingAsync(source.RtId, plan with { Slices = dataSlices },
                           location, Options, ct))
        {
            results.Add(result);
        }

        await afterData();

        results.Should().OnlyContain(r => r.IsSuccess, "every slice was copied");

        // The plan as the files stand: each slice carries the rows the copy reported (what a load verifies against).
        var copied = results.ToDictionary(r => r.Slice.Key, r => r.CopiedRows, StringComparer.Ordinal);
        var copiedPlan = plan with
        {
            Slices = plan.Slices
                .Select(s => copied.TryGetValue(s.Key, out var rows) && rows != s.RowCount
                    ? new ArchiveCopySlice(s.Table, s.Key, rows, s.FromUtc, s.ToUtc, s.StagedBytes)
                    : s)
                .ToList()
        };
        return (copiedPlan, "one pass (no repeat rule)");
    }
}

/// <summary>The placement matrix with the sweep grace period off.</summary>
[Collection(PlacementMatrixGraceOffCollection.Name)]
public sealed class PlacementMatrixGraceOffTests(PlacementMatrixGraceOffFixture fixture, ITestOutputHelper output)
    : PlacementMatrixCases(fixture, output, graceOn: false);

/// <summary>The placement matrix with the default sweep grace period.</summary>
[Collection(PlacementMatrixGraceOnCollection.Name)]
public sealed class PlacementMatrixGraceOnTests(PlacementMatrixGraceOnFixture fixture, ITestOutputHelper output)
    : PlacementMatrixCases(fixture, output, graceOn: true);
