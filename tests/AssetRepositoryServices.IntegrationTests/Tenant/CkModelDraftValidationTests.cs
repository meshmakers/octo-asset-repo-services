using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
            AssertNoServerPath(result);

            result.Errors.Should().BeEmpty();
            result.IsValid.Should().BeTrue();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindSource);
            result.ModelId.Should().Be("DraftValidation-1.0.0");
            result.IsBlocked.Should().BeFalse();
            var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            (await tenant.IsCkModelExistingAsync(new CkModelId("DraftValidation-1.0.0"))).Should().BeFalse();
        });
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("draft.bin", "application/octet-stream")]
    [InlineData("draft", null)]
    public async Task ASourceZip_WithoutZipNameOrContentType_IsCompiledByItsZipHeader(string fileName,
        string? contentType)
    {
        await WithTenantAsync("ckv-hdr", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var zip = SourceZip(("ckModel.yaml", CkModelYaml), ("attributes/attributes.yaml", AttributesYaml),
                ("types/pump.yaml", PumpYaml("FlowRate")));

            var result = await sut.ValidateAsync(tenantId, zip, fileName, contentType, ct);
            AssertNoServerPath(result);

            result.Errors.Should().BeEmpty();
            result.IsValid.Should().BeTrue();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindSource);
            result.ModelId.Should().Be("DraftValidation-1.0.0");
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASourceZipThatValidated_IsImportedThroughImportCk_AsTheModelValidateCkJudged()
    {
        await WithTenantAsync("ckv-imp", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            var draft = SourceZip(("ConstructionKit/ckModel.yaml", CkModelYaml),
                ("ConstructionKit/attributes/attributes.yaml", AttributesYaml),
                ("ConstructionKit/types/pump.yaml", PumpYaml("FlowRate"))).ToArray();

            // 1. ValidateCk judges the draft.
            var validation = await sut.ValidateAsync(tenantId, new MemoryStream(draft), "draft.zip", "application/zip", ct);
            validation.IsValid.Should().BeTrue();
            validation.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindSource);

            // 2. ImportCk with the same bytes: compiled on the validator's path, the compiled model is cached and queued.
            var cache = A.Fake<IDistributedCacheService>();
            byte[]? cached = null;
            string? cachedName = null;
            A.CallTo(() => cache.CreateStreamAsync(A<string>._, A<Stream>._, A<string>._, A<string>._, A<TimeSpan?>._))
                .ReturnsLazily((string _, Stream stream, string _, string fileName, TimeSpan? _) =>
                {
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    cached = copy.ToArray();
                    cachedName = fileName;
                    return Task.FromResult("cache-key");
                });
            var importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
            A.CallTo(() => importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
                .Returns(new JobCreatedResponse("job-1"));
            var controller = CreateController(sut, cache, importCk, tenantId);

            var result = await controller.ImportCk(new FormFile(new MemoryStream(draft), 0, draft.Length, "file",
                "draft.zip") { Headers = new HeaderDictionary(), ContentType = "application/zip" });

            result.Should().BeOfType<OkObjectResult>();
            A.CallTo(() => importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
                .MustHaveHappenedOnceExactly();
            cached.Should().NotBeNull();

            // 3. The import job (bot-services ImportCkModelCommand) reads the cached file as a compiled model and imports it.
            var operationResult = new OperationResult();
            var compiled = await fixture.GetService<ICkSerializer>()
                .DeserializeCompiledModelRootAsync(new MemoryStream(cached!), cachedName!, operationResult);
            operationResult.HasErrors.Should().BeFalse();
            var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            await tenant.ImportCkModelAsync(compiled);

            compiled.ModelId.FullName.Should().Be(validation.ModelId);
            (await tenant.IsCkModelExistingAsync(new CkModelId("DraftValidation-1.0.0"))).Should().BeTrue();
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASourceZipWithAnUnknownAttribute_IsRefusedByImportCk_WithTheValidateCkFindings()
    {
        await WithTenantAsync("ckv-imx", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            var draft = SourceZip(("ConstructionKit/ckModel.yaml", CkModelYaml),
                ("ConstructionKit/attributes/attributes.yaml", AttributesYaml),
                ("ConstructionKit/types/pump.yaml", PumpYaml("NoSuchAttribute"))).ToArray();
            var validation = await sut.ValidateAsync(tenantId, new MemoryStream(draft), "draft.zip", "application/zip", ct);
            validation.IsValid.Should().BeFalse();

            var cache = A.Fake<IDistributedCacheService>();
            var importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
            var controller = CreateController(sut, cache, importCk, tenantId);

            var result = await controller.ImportCk(new FormFile(new MemoryStream(draft), 0, draft.Length, "file",
                "draft.zip") { Headers = new HeaderDictionary(), ContentType = "application/zip" });

            var refused = result.Should().BeOfType<ObjectResult>().Subject;
            refused.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            var problem = refused.Value.Should().BeOfType<ProblemDetails>().Subject;
            problem.Extensions["code"].Should().Be(CkModelCompileFailedError.Code);
            var errors = ((IEnumerable<CkModelValidationMessageDto>)problem.Extensions["errors"]!).ToList();
            errors.Select(e => (e.MessageNumber, e.File, e.ElementPath)).Should()
                .BeEquivalentTo(validation.Errors.Select(e => (e.MessageNumber, e.File, e.ElementPath)));
            errors.Should().Contain(e => e.File == "types/pump.yaml");
            JsonSerializer.Serialize(problem).Should().NotContain("octo-ck-validate-");
            A.CallTo(cache).MustNotHaveHappened();
            A.CallTo(importCk).MustNotHaveHappened();
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
            AssertNoServerPath(result);

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
            AssertNoServerPath(result);

            result.IsValid.Should().BeFalse();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindCompiled);
            var refusal = result.Errors.Should().Contain(e => e.MessageNumber == 8492).Subject;
            refusal.Parameters["reason"].Should().Be("dependency-resolution");
            refusal.Message.Should().Contain("NotInstalledAnywhere");
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMissingDependency_OfASourceDraft_IsADependencyError_WithoutTheTempFolder()
    {
        await WithTenantAsync("ckv-srcdep", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            const string needsMissing = """
                $schema: https://schemas.meshmakers.cloud/construction-kit-meta.schema.json
                modelId: DraftNeedsMissing-1.0.0
                dependencies:
                  - System-[2.5,3.0)
                  - NotInstalledAnywhere-[1.0,2.0)
                """;
            using var zip = SourceZip(("ckModel.yaml", needsMissing), ("attributes/attributes.yaml", AttributesYaml));

            var result = await sut.ValidateAsync(tenantId, zip, "draft.zip", "application/zip", ct);
            AssertNoServerPath(result);

            result.IsValid.Should().BeFalse();
            result.SourceKind.Should().Be(CkModelValidationResponseDto.SourceKindSource);
            // Message 1 names the draft folder itself as its location: no file, never the temp folder name.
            var unknown = result.Errors.Should().Contain(e => e.MessageNumber == CkModelDraftValidator.UnknownCkModelNumber)
                .Subject;
            unknown.File.Should().BeNull();
            unknown.Message.Should().Contain("NotInstalledAnywhere");
            result.Errors.Where(e => e.MessageNumber == CkModelDraftValidator.UnreadableDraftNumber)
                .Should().NotBeEmpty()
                .And.OnlyContain(e => e.Parameters["reason"] == CkModelDraftValidator.ReasonDependencyResolution);
        });
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACompiledModelWithBrokenYaml_IsAnError_NotAFailedCall()
    {
        await WithTenantAsync("ckv-yaml", TestContext.Current.CancellationToken, async (tenantId, sut, ct) =>
        {
            using var broken = new MemoryStream("modelId: X-1.0.0\n  types: [\n"u8.ToArray());

            var result = await sut.ValidateAsync(tenantId, broken, "draft.yaml", "application/yaml", ct);
            AssertNoServerPath(result);

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
            AssertNoServerPath(result);

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
            AssertNoServerPath(result);

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
            AssertNoServerPath(result);

            result.IsValid.Should().BeFalse();
            var texts = result.Errors.Concat(result.Warnings)
                .SelectMany(m => new[] { m.Message, m.File ?? string.Empty }.Concat(m.Parameters.Values))
                .ToList();
            texts.Should().NotContain(t => t.Contains("octo-ck-validate-"));
            texts.Should().NotContain(t => t.Contains(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
        });
    }

    /// <summary>
    ///     Guard for every case: the serialized response (what REST and MCP return) names neither the server's temp folder
    ///     nor the temp path.
    /// </summary>
    private static void AssertNoServerPath(CkModelValidationResponseDto result)
    {
        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("octo-ck-validate-");
        json.Should().NotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar).Replace("\\", "\\\\"));
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

    /// <summary>
    ///     The real <c>ModelsController</c> with the real validator, serializers, tenant context and upgrade guard; only the
    ///     distributed cache and the import job queue are fakes (the job runs in bot-services).
    /// </summary>
    private ModelsController CreateController(CkModelDraftValidator validator, IDistributedCacheService cache,
        ICommandClient<ImportCkCommandRequest> importCk, string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        var catalog = A.Fake<ICatalogService>();
        A.CallTo(() => catalog.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult { ModelResultItems = [], TotalCount = 0, SkippedCount = 0, TakeCount = 500 });
        var authorization = A.Fake<IAuthorizationService>();
        A.CallTo(() => authorization.AuthorizeAsync(A<ClaimsPrincipal>._, A<object?>._, A<string>._))
            .Returns(AuthorizationResult.Success());
        var controller = new ModelsController(cache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            importCk,
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            catalog,
            fixture.GetService<ICkJsonSerializer>(),
            systemContext,
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>(),
            new CkModelLibraryStatusService(catalog, systemContext),
            NullLogger<ModelsController>.Instance,
            fixture.GetService<ICkSerializer>(),
            authorization,
            validator);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = tenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-4711"), new Claim("role", "DataModelManagement")], "Bearer"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
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
