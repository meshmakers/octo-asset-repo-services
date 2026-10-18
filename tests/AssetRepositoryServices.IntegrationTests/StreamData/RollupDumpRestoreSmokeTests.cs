using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#6968 — the dump and load of a rollup through a <c>file://</c> staging location on the single-node CrateDB of the
///     tests: no recompute runs while it is dumped, so the dump takes one attempt and the restored rollup validates ok and
///     reads the same values. The base cell of the placement matrix and the proof that the staging location works in CI.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupDumpRestoreSmokeTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ADumpWithoutAConcurrentRecompute_RestoresConsistently_InOneAttempt()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var source = await kit.CreateRollupAsync("DumpSmokeSource");
        await kit.RecomputeAsync(source);
        var reference = await kit.ReadActiveValuesAsync(source);

        var restored = await DumpAndLoadAsync(kit, source, "DumpSmokeTarget");

        restored.Dump.Attempts.Should().Be(1);
        restored.Dump.MapReadAtUtc.Should().NotBeNull();
        restored.Dump.MapReadAtUtc.Should().BeOnOrBefore(restored.Dump.LastDataCopiedAtUtc!.Value);
        restored.Load.RowCountVerified.Should().BeTrue();

        var report = await kit.Repository.ValidateRollupsAsync([restored.Target.RtId], TestContext.Current.CancellationToken);
        report.IsOk.Should().BeTrue();
        (await kit.ReadActiveValuesAsync(restored.Target)).Should().Equal(reference);
    }

    /// <summary>
    ///     A <c>file://</c> staging area inside the CrateDB container with the folders the dump of <paramref name="source" />
    ///     writes into: CrateDB creates the files but not the directories of a <c>COPY … TO DIRECTORY</c>.
    /// </summary>
    internal static async Task<ArchiveCopyStagingLocation> PrepareStagingAsync(
        RollupTestKit kit, RollupHandle source, ArchiveCopyOptions options, CancellationToken ct)
    {
        var root = $"/tmp/octo-dump-{Guid.NewGuid():N}";
        var plan = await kit.Repository.PlanArchiveCopyAsync(source.RtId, options, ct);
        var folders = plan.Slices.Select(s => $"{root}/{s.GetStagingPath(plan.ArchiveRtId)}").ToArray();
        await SharedCrateDbContainer.ExecAsync(["mkdir", "-p", root, .. folders]);

        // The command runs as root, CrateDB as the user crate: it must be allowed to write and to read the folders.
        await SharedCrateDbContainer.ExecAsync("chmod", "-R", "a+rwx", root);
        return new ArchiveCopyStagingLocation($"file://{root}", []);
    }

    /// <summary>Dumps <paramref name="source" /> to a file staging area and loads it into a fresh rollup of the same definition.</summary>
    internal async Task<(ArchiveCopyDumpResult Dump, ArchiveCopyLoadResult Load, RollupHandle Target)> DumpAndLoadAsync(
        RollupTestKit kit, RollupHandle source, string targetName, ArchiveCopyOptions? options = null)
    {
        var ct = TestContext.Current.CancellationToken;
        options ??= new ArchiveCopyOptions();
        var location = await PrepareStagingAsync(kit, source, options, ct);

        var dump = await kit.Repository.DumpArchiveToStagingAsync(source.RtId, location, options, ct);
        dump.IsSuccess.Should().BeTrue(string.Join(" | ", dump.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));

        var target = await kit.CreateRollupAsync(targetName);
        var load = await kit.Repository.LoadArchiveFromStagingAsync(target.RtId, dump.Plan, location, options, ct);
        load.IsSuccess.Should().BeTrue(string.Join(" | ", load.Slices.Select(r => $"{r.Slice.Key}: {r.Status} {r.Message}")));
        await kit.RefreshAsync(target.Table);
        await kit.RefreshAsync(target.GenMapTable);
        return (dump, load, target);
    }
}
