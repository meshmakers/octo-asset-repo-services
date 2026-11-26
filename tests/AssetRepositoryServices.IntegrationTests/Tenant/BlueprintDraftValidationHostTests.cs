using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.BlueprintValidation;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     The host registration of <c>POST blueprints/validate</c>: the real service collection resolves the draft validation
///     with the engine's validator, so the endpoint is never left without it (it would answer 500 "not configured").
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class BlueprintDraftValidationHostTests(BlueprintUpdateFlowFixture fixture)
{
    private const string Manifest = """
        blueprintId: HostValidation-1.0.0
        description: Draft validated through the host registration
        """;

    [Fact]
    public async Task TheHostResolvesTheDraftValidation_AndAZipDraftIsJudged()
    {
        var sut = fixture.GetService<IBlueprintDraftValidationService>();

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("blueprint.yaml");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync(Manifest);
        }

        zip.Position = 0;
        var result = await sut.ValidateAsync(zip, "draft.zip", TestContext.Current.CancellationToken);

        result.TotalFindings.Should().Be(result.Findings.Count);
        result.Findings.Should().NotContain(f => f.Code == 167, "a readable zip is never 'unreadable'");
    }

    [Fact]
    public async Task AGarbageUpload_Is167_NotAFailedCall()
    {
        var sut = fixture.GetService<IBlueprintDraftValidationService>();

        using var garbage = new MemoryStream([1, 2, 3, 4, 5]);
        var result = await sut.ValidateAsync(garbage, "draft.zip", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Findings.Should().ContainSingle(f => f.Code == 167);
    }
}
