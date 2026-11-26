using System.IO.Compression;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.BlueprintValidation;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UnitTests.Services;

/// <summary>
///     <c>POST {tenantId}/v1/blueprints/validate</c> - the upload (zip or blueprint.yaml) goes to the engine's
///     shared draft validator with the CK catalog check on; findings keep their codes, errors first, bounded.
/// </summary>
public class BlueprintDraftValidationServiceTests
{
    private readonly IBlueprintDraftValidator _validator = A.Fake<IBlueprintDraftValidator>();
    private IBlueprintFileSource? _seenSource;
    private BlueprintValidationOptions? _seenOptions;

    public BlueprintDraftValidationServiceTests()
    {
        A.CallTo(() => _validator.ValidateAsync(A<IBlueprintFileSource>._, A<BlueprintValidationOptions?>._,
                A<CancellationToken>._))
            .ReturnsLazily((IBlueprintFileSource source, BlueprintValidationOptions? options, CancellationToken _) =>
            {
                _seenSource = source;
                _seenOptions = options;
                return Task.FromResult(new BlueprintDraftValidationResult(
                    new BlueprintMetaRootDto { BlueprintId = new BlueprintId("Draft", "1.0.0") },
                    [
                        new OperationMessage(MessageLevel.Warning, "seed-data/a.yaml", 159, "reuse"),
                        new OperationMessage(MessageLevel.Error, "blueprint.yaml", 163, "not in catalogs",
                            new Dictionary<string, string> { ["modelId"] = "Acme.Unknown" })
                    ]));
            });
    }

    [Fact]
    public async Task Zip_IsValidatedWithTheCatalogCheck_ErrorsFirst()
    {
        await using var zip = Zip(("Draft-1.0.0/blueprint.yaml", "blueprintId: Draft-1.0.0"),
            ("Draft-1.0.0/seed-data/a.yaml", "entities: []"));

        var result = await new BlueprintDraftValidationService(_validator).ValidateAsync(zip, "Draft-1.0.0.zip",
            TestContext.Current.CancellationToken);

        _seenOptions!.CkModelCatalogs.Should().NotBeNull().And.BeEmpty("all catalogs of the asset repository");
        (await _seenSource!.ReadTextAsync("blueprint.yaml", TestContext.Current.CancellationToken))
            .Should().Contain("Draft-1.0.0");
        result.IsValid.Should().BeFalse();
        result.BlueprintId.Should().Be("Draft-1.0.0");
        result.TotalFindings.Should().Be(2);
        result.Findings[0].Code.Should().Be(163);
        result.Findings[0].ModelId.Should().Be("Acme.Unknown");
        result.Findings[1].Code.Should().Be(159);
    }

    [Fact]
    public async Task SingleYaml_IsTheManifest()
    {
        await using var yaml = new MemoryStream(Encoding.UTF8.GetBytes("blueprintId: Draft-1.0.0"));

        await new BlueprintDraftValidationService(_validator).ValidateAsync(yaml, "blueprint.yaml",
            TestContext.Current.CancellationToken);

        (await _seenSource!.ReadTextAsync("blueprint.yaml", TestContext.Current.CancellationToken))
            .Should().Be("blueprintId: Draft-1.0.0");
    }

    [Fact]
    public async Task BrokenZip_IsFinding167_AndTheValidatorIsNotCalled()
    {
        await using var notAZip = new MemoryStream(Encoding.UTF8.GetBytes("PK not really"));

        var result = await new BlueprintDraftValidationService(_validator).ValidateAsync(notAZip, "x.zip",
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Findings.Should().ContainSingle().Which.Code.Should().Be(BlueprintDraftValidationCodes.DraftUnreadable);
        A.CallTo(() => _validator.ValidateAsync(A<IBlueprintFileSource>._, A<BlueprintValidationOptions?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Map_BoundsTheList()
    {
        var findings = Enumerable.Range(0, BlueprintDraftValidationResponseDto.MaxFindings + 5)
            .Select(_ => new OperationMessage(MessageLevel.Warning, null, 159, "w")).ToList();

        var dto = BlueprintDraftValidationService.Map(new BlueprintDraftValidationResult(null, findings));

        dto.Findings.Should().HaveCount(BlueprintDraftValidationResponseDto.MaxFindings);
        dto.Truncated.Should().BeTrue();
        dto.TotalFindings.Should().Be(BlueprintDraftValidationResponseDto.MaxFindings + 5);
        dto.BlueprintId.Should().BeNull();
    }

    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
