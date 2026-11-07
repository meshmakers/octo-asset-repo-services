using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Services.CkModelValidation;

/// <summary>
///     AB#8492: the pure parts of <see cref="CkModelDraftValidator" />: element path and draft-relative file of a message,
///     finding the source folder in a zip and the safe extraction (no path outside the folder).
/// </summary>
public class CkModelDraftValidatorTests
{
    [Fact]
    public void ElementPath_IsTheContainerAndTheMember()
    {
        CkModelDraftValidator.ElementPath(new Dictionary<string, string>
                { ["ckTypeId"] = "Demo/Pump-1", ["ckAttributeId"] = "Demo/FlowRate-1" })
            .Should().Be("Demo/Pump-1.Demo/FlowRate-1");
        CkModelDraftValidator.ElementPath(new Dictionary<string, string> { ["modelId"] = "Basic-[2.0,3.0)" })
            .Should().Be("Basic-[2.0,3.0)");
        CkModelDraftValidator.ElementPath(new Dictionary<string, string> { ["attributeName"] = "FlowRate" })
            .Should().Be("FlowRate");
        CkModelDraftValidator.ElementPath(new Dictionary<string, string> { ["reason"] = "x" }).Should().BeNull();
    }

    [Fact]
    public void RelativeFile_NeverReturnsAServerPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "octo-ck-validate-x");
        CkModelDraftValidator.RelativeFile(Path.Combine(root, "types", "pump.yaml"), root).Should().Be("types/pump.yaml");
        CkModelDraftValidator.RelativeFile("/elsewhere/secret/ck.yaml", root).Should().Be("ck.yaml");
        CkModelDraftValidator.RelativeFile("/srv/x/model.json", null).Should().Be("model.json");
        CkModelDraftValidator.RelativeFile("-", root).Should().BeNull();
        CkModelDraftValidator.RelativeFile(null, root).Should().BeNull();
    }

    [Fact]
    public void ToDto_KeepsNumberLevelAndParameters()
    {
        var dto = CkModelDraftValidator.ToDto(new OperationMessage(MessageLevel.Error, null, 91, "text",
            new Dictionary<string, string> { ["modelId"] = "Demo-1.0.0" }), null);

        dto.MessageNumber.Should().Be(91);
        dto.Level.Should().Be("Error");
        dto.ElementPath.Should().Be("Demo-1.0.0");
        dto.Parameters.Should().ContainKey("modelId");
    }

    [Fact]
    public void TryFindSourceRoot_TakesTheShallowestCkModelYaml()
    {
        using var zip = Zip(("x/ConstructionKit/ckModel.yaml", "a"), ("x/ConstructionKit/deps/y/ckModel.yaml", "b"),
            ("x/ConstructionKit/types/t.yaml", "c"));

        CkModelDraftValidator.TryFindSourceRoot(zip, out var prefix).Should().BeTrue();
        prefix.Should().Be("x/ConstructionKit/");
    }

    [Fact]
    public void TryFindSourceRoot_ACompiledZip_IsNoSource()
    {
        using var zip = Zip(("ck-demo-1.json", "{}"));

        CkModelDraftValidator.TryFindSourceRoot(zip, out _).Should().BeFalse();
    }

    [Fact]
    public void ExtractSource_WritesOnlyBelowTheFolder()
    {
        var target = Path.Combine(Path.GetTempPath(), $"ckv-unit-{Guid.NewGuid():N}");
        try
        {
            using var zip = Zip(("CK/ckModel.yaml", "a"), ("CK/types/t.yaml", "b"), ("other/ignored.yaml", "c"));

            CkModelDraftValidator.ExtractSource(zip, "CK/", target);

            File.Exists(Path.Combine(target, "ckModel.yaml")).Should().BeTrue();
            File.Exists(Path.Combine(target, "types", "t.yaml")).Should().BeTrue();
            Directory.GetFiles(target, "*", SearchOption.AllDirectories).Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
    }

    [Theory]
    [InlineData("../evil.yaml")]
    [InlineData("types/../../evil.yaml")]
    public void ExtractSource_RefusesAPathOutsideTheFolder(string entry)
    {
        var target = Path.Combine(Path.GetTempPath(), $"ckv-unit-{Guid.NewGuid():N}");
        try
        {
            using var zip = Zip(("ckModel.yaml", "a"), (entry, "x"));

            var act = () => CkModelDraftValidator.ExtractSource(zip, string.Empty, target);

            act.Should().Throw<InvalidOperationException>().WithMessage("*not allowed*");
            File.Exists(Path.Combine(Path.GetTempPath(), "evil.yaml")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
    }

    [Fact]
    public void ExtractSource_RefusesTooManyEntries()
    {
        var entries = Enumerable.Range(0, CkModelDraftValidator.MaxZipEntries + 1)
            .Select(i => ($"types/t{i}.yaml", "x")).ToArray();
        using var zip = Zip(entries);

        var act = () => CkModelDraftValidator.ExtractSource(zip, string.Empty,
            Path.Combine(Path.GetTempPath(), $"ckv-unit-{Guid.NewGuid():N}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*entries*");
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
