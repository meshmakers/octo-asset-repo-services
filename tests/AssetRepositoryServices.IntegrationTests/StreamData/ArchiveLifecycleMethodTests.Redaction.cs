using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     <c>Archive.RedactHiddenColumns-1</c> (System.StreamData 1.27.0) and <c>-2</c> (1.29.0, with the confirmed columns)
///     through the real gateway: they need StreamDataAdmin and the explicit data-loss confirmation, and on an archive without a
///     Hidden column they drop nothing and keep the status; <c>-2</c> refuses a confirmed set that is not the Hidden set.
///     The drop itself against CrateDB is covered in the engine (engine-mongodb integration tests).
/// </summary>
public partial class ArchiveLifecycleMethodTests
{
    private static string RedactCall(OctoObjectId rtId, bool confirm) =>
        $$"""{{Raw}} { redactHiddenColumns(rtId: "{{rtId}}", input: { confirmDataLoss: {{(confirm ? "true" : "false")}} }) { invocationId result { archiveRtId status removedColumns } } }""";

    private static string Redact2Call(OctoObjectId rtId, string expectedColumns) =>
        $$"""{{Raw}} { redactHiddenColumns2(rtId: "{{rtId}}", input: { confirmDataLoss: true{{expectedColumns}} }) { invocationId result { archiveRtId status removedColumns } } }""";

    [Fact]
    public async Task RedactHiddenColumns2_ConfirmingNoColumn_OnAnArchiveWithoutAHiddenColumn_DropsNothing()
    {
        var archive = await CreateRawArchiveAsync("Redact2None", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {Redact2Call(archive, ", expectedColumns: []")} }} }}", user: Admin());

        var node = Data(result).SelectToken($"runtime.{Raw}.redactHiddenColumns2.result")!;
        node.Value<string>("archiveRtId").Should().Be(archive.ToString());
        Normalize(node.Value<string>("status")!).Should().Be("ACTIVATED");
        node["removedColumns"]!.Values<string>().Should().BeEmpty();
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task RedactHiddenColumns2_ConfirmingAColumnThatIsNotHidden_IsRefusedWithBothSets_AndChangesNothing()
    {
        var archive = await CreateRawArchiveAsync("Redact2Changed", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {Redact2Call(archive, ", expectedColumns: [\"Voltage\"]")} }} }}", user: Admin());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ArchiveMethodErrors.RedactionColumnsChanged);
        var parameters = MethodErrorParameters(result);
        parameters.Value<string>("expectedColumns").Should().Be("Voltage");
        parameters.Value<string>("currentColumns").Should().BeEmpty();
        var snapshot = (await SnapshotAsync(archive))!;
        snapshot.Columns.Select(c => c.Path).Should().Equal("Voltage");
        snapshot.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task RedactHiddenColumns2_WithoutTheConfirmedColumns_IsRefused_AndChangesNothing()
    {
        var archive = await CreateRawArchiveAsync("Redact2Missing", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {Redact2Call(archive, string.Empty)} }} }}", user: Admin());

        result.Errors.Should().NotBeNullOrEmpty();
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task RedactHiddenColumns2_WithoutStreamDataAdmin_IsMethodForbidden()
    {
        var archive = await CreateRawArchiveAsync("Redact2Forbidden", activate: true);

        var result = await _fixture.ExecuteGraphQlAsync(
            $"mutation {{ runtime {{ {Redact2Call(archive, ", expectedColumns: []")} }} }}", user: WithoutRole());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
    }

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
