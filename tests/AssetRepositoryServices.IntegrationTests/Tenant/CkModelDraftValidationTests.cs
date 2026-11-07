using System.IO.Compression;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#8492: <see cref="CkModelDraftValidator" /> against the real compiler, catalogs, tenant context and database. A
///     zipped source <c>ConstructionKit/</c> folder is compiled and checked against the tenant; errors carry the draft file and
///     the element; a missing dependency is an error, not a failed call; nothing is imported.
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class CkModelDraftValidationTests(BlueprintUpdateFlowFixture fixture)
{
    private const int TestTimeoutMs = 180_000;

    private const string CkModelYaml = """
        $schema: https://schemas.meshmakers.cloud/construction-kit-meta.schema.json
        modelId: DraftValidation-1.0.0
        dependencies:
          - System-[2.5,3.0)
        """;

    private const string AttributesYaml = """
        $schema: https://schemas.meshmakers.cloud/construction-kit-elements.schema.json
        attributes:
          - id: FlowRate
            valueType: Double
        """;

    private static string PumpYaml(string attributeId) => $"""
        $schema: https://schemas.meshmakers.cloud/construction-kit-elements.schema.json
        types:
          - typeId: Pump
            derivedFromCkTypeId: ${"{"}System{"}"}/Entity
            attributes:
              - id: ${"{"}System{"}"}/Name
                name: Name
              - id: ${"{"}this{"}"}/{attributeId}
                name: FlowRate
                isOptional: true
        """;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AValidSourceFolder_IsValid_AndNothingIsImported()
    {
        await WithTenantAsync("ckv-ok", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var zip = SourceZip(("ckModel.yaml", CkModelYaml), ("attributes/attributes.yaml", AttributesYaml),
                ("types/pump.yaml", PumpYaml("FlowRate")));

            var result = await sut.ValidateAsync(tenantId, zip, "draft.zip", "application/zip", ct);

            result.Errors.Should().BeEmpty();
            result.IsValid.Should().BeTrue();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindSource);
            result.ModelId.Should().Be("DraftValidation-1.0.0");
            result.IsBlocked.Should().BeFalse();
            var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            (await tenant.IsCkModelExistingAsync(new CkModelId("DraftValidation-1.0.0"))).Should().BeFalse();
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnknownAttribute_IsAnError_WithTheDraftFile_AndTheElement()
    {
        await WithTenantAsync("ckv-err", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var zip = SourceZip(("ConstructionKit/ckModel.yaml", CkModelYaml),
                ("ConstructionKit/attributes/attributes.yaml", AttributesYaml),
                ("ConstructionKit/types/pump.yaml", PumpYaml("NoSuchAttribute")));

            var result = await sut.ValidateAsync(tenantId, zip, "draft.zip", "application/zip", ct);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Message.Contains("NoSuchAttribute"));
            var error = result.Errors.First(e => e.Message.Contains("NoSuchAttribute"));
            error.File.Should().Be("types/pump.yaml");
            error.ElementPath.Should().Contain("Pump");
            // A server path never leaves the service.
            result.Errors.Should().NotContain(e => e.Message.Contains(Path.GetTempPath()) ||
                                                   (e.File != null && Path.IsPathRooted(e.File)));
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMissingDependency_OfACompiledModel_IsAnError_NotAFailedCall()
    {
        await WithTenantAsync("ckv-dep", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            var compiled = new CkCompiledModelRoot
            {
                ModelId = new CkModelId("DraftNeedsMissing-1.0.0"),
                Dependencies = [new CkModelId("NotInstalledAnywhere-1.0.0")],
                Types = []
            };
            using var yaml = await CompiledYamlAsync(compiled);

            var result = await sut.ValidateAsync(tenantId, yaml, "draft.yaml", "application/yaml", ct);

            result.IsValid.Should().BeFalse();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindCompiled);
            var refusal = result.Errors.Should().Contain(e => e.MessageNumber == 8492).Subject;
            refusal.Parameters["reason"].Should().Be("dependency-resolution");
            refusal.Message.Should().Contain("NotInstalledAnywhere");
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACompiledModelWithBrokenYaml_IsAnError_NotAFailedCall()
    {
        await WithTenantAsync("ckv-yaml", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var broken = new MemoryStream("modelId: X-1.0.0\n  types: [\n"u8.ToArray());

            var result = await sut.ValidateAsync(tenantId, broken, "draft.yaml", "application/yaml", ct);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle().Which.Parameters["reason"].Should().Be("unreadable-draft");
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnreadableFile_IsAnError()
    {
        await WithTenantAsync("ckv-bad", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var garbage = new MemoryStream("not a zip"u8.ToArray());

            var result = await sut.ValidateAsync(tenantId, garbage, "draft.zip", "application/zip", ct);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle().Which.Parameters["reason"].Should().Be("unreadable-draft");
            result.SourceKind.Should().BeNull();
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AZipEntryOutsideTheFolder_IsRefused_AndNothingIsWrittenOutside()
    {
        await WithTenantAsync("ckv-slip", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            var outside = $"ckv-slip-{Guid.NewGuid():N}.yaml";
            using var zip = SourceZip(("ckModel.yaml", CkModelYaml), ($"../{outside}", "x: 1"));

            var result = await sut.ValidateAsync(tenantId, zip, "draft.zip", "application/zip", ct);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not allowed");
            File.Exists(Path.Combine(Path.GetTempPath(), outside)).Should().BeFalse();
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADuplicateZipEntry_IsRefused_WithoutAServerPath()
    {
        await WithTenantAsync("ckv-dup", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            // ZipArchive reads duplicate names; the second extraction fails with an IOException naming the temp file.
            using var zip = SourceZip(("ckModel.yaml", CkModelYaml), ("types/pump.yaml", "x: 1"),
                ("types/pump.yaml", "x: 2"));

            var result = await sut.ValidateAsync(tenantId, zip, "draft.zip", "application/zip", ct);

            result.IsValid.Should().BeFalse();
            var texts = result.Errors.Concat(result.Warnings)
                .SelectMany(m => new[] { m.Message, m.File ?? string.Empty }.Concat(m.Parameters.Values))
                .ToList();
            texts.Should().NotContain(t => t.Contains("octo-ck-validate-"));
            texts.Should().NotContain(t => t.Contains(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
        });
    }

    private CkModelDraftValidator CreateValidator()
    {
        var systemContext = fixture.GetSystemContext();
        var catalog = A.Fake<ICatalogService>();
        A.CallTo(() => catalog.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult { ModelResultItems = [], TotalCount = 0, SkippedCount = 0, TakeCount = 500 });
        return new CkModelDraftValidator(fixture.GetService<ICompilerService>(), fixture.GetService<ICkSerializer>(),
            systemContext, new CkModelLibraryStatusService(catalog, systemContext),
            NullLogger<CkModelDraftValidator>.Instance);
    }

    private async Task WithTenantAsync(string prefix, CancellationToken ct,
        Func<string, CkModelDraftValidator, CancellationToken, Task> body)
    {
        var tenantId = await fixture.CreateTenantAsync(prefix);
        try
        {
            await body(tenantId, CreateValidator(), ct);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task<MemoryStream> CompiledYamlAsync(CkCompiledModelRoot compiled)
    {
        var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            await fixture.GetService<ICkSerializer>().SerializeAsync(writer, compiled);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream SourceZip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
