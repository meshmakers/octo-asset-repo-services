using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     <c>Archive.RedactHiddenColumns-1</c> (System.StreamData 1.27.0) through the real gateway: it needs StreamDataAdmin and
///     the explicit data-loss confirmation, and on an archive without a Hidden column it drops nothing and keeps the status.
///     The drop itself against CrateDB is covered in the engine (engine-mongodb integration tests).
/// </summary>
public partial class ArchiveLifecycleMethodTests
{
    private static string RedactCall(OctoObjectId rtId, bool confirm) =>
        $$"""{{Raw}} { redactHiddenColumns(rtId: "{{rtId}}", input: { confirmDataLoss: {{(confirm ? "true" : "false")}} }) { invocationId result { archiveRtId status removedColumns } } }""";

    [Fact]
    public async Task RedactHiddenColumns_WithoutAHiddenColumn_DropsNothing_AndKeepsTheStatus()
    {
        var archive = await CreateRawArchiveAsync("Redact", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {RedactCall(archive, confirm: true)} }} }}", user: Admin());

        var node = Data(result).SelectToken($"runtime.{Raw}.redactHiddenColumns.result")!;
        node.Value<string>("archiveRtId").Should().Be(archive.ToString());
        Normalize(node.Value<string>("status")!).Should().Be("ACTIVATED");
        node["removedColumns"]!.Values<string>().Should().BeEmpty();
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task RedactHiddenColumns_WithoutTheConfirmation_IsRefused_AndChangesNothing()
    {
        var archive = await CreateRawArchiveAsync("RedactNoConfirm", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {RedactCall(archive, confirm: false)} }} }}", user: Admin());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ArchiveMethodErrors.ConfirmationRequired);
        MethodErrorParameters(result).Value<string>("archiveRtId").Should().Be(archive.ToString());
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task RedactHiddenColumns_WithoutStreamDataAdmin_IsMethodForbidden()
    {
        var archive = await CreateRawArchiveAsync("RedactForbidden", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {RedactCall(archive, confirm: true)} }} }}",
            user: WithoutRole());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
    }
}
