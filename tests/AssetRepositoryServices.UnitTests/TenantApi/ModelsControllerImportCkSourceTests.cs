using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     <c>Models/ImportCk</c> with a source draft (a zip of the <c>ConstructionKit/</c> folder): the draft is compiled by the
///     validator's source path (engine compiler faked here; the real compile is covered by the integration test), the
///     compiled model is what the service-managed refusal and the upgrade guard judge and what is cached for the import
///     job, and compile errors are refused with 400 before anything is cached or queued. Whether an upload is a source
///     draft is decided by <c>ckModel.yaml</c> alone.
/// </summary>
public class ModelsControllerImportCkSourceTests
{
    private const string Tenant = "acme";

    private readonly IDistributedCacheService _distributedCache = A.Fake<IDistributedCacheService>();
    private readonly ICommandClient<ImportCkCommandRequest> _importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
    private readonly ICkModelLibraryStatusService _libraryStatusService = A.Fake<ICkModelLibraryStatusService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ICompilerService _compiler = A.Fake<ICompilerService>();
    private readonly ICkSerializer _ckSerializer;
    private readonly ModelsController _controller;
    private readonly List<(byte[] Bytes, string ContentType, string FileName)> _cached = [];
    private readonly List<IReadOnlyList<CkCompiledModelRoot>> _judged = [];
    private readonly List<string> _compiledFolders = [];
    private IReadOnlyList<CkUpgradeBlockerDto> _blockers = [];

    public ModelsControllerImportCkSourceTests()
    {
        var services = new ServiceCollection().AddLogging().AddConstructionKit().BuildServiceProvider();
        _ckSerializer = services.GetRequiredService<ICkSerializer>();
        var ckJsonSerializer = services.GetRequiredService<ICkJsonSerializer>();

        A.CallTo(() => _libraryStatusService.EvaluateFileImportBlockersAsync(A<string>._,
                A<IReadOnlyList<CkCompiledModelRoot>>._, A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<CkCompiledModelRoot> models, CancellationToken _) =>
            {
                _judged.Add(models);
                return Task.FromResult(_blockers);
            });
        A.CallTo(() => _distributedCache.CreateStreamAsync(A<string>._, A<Stream>._, A<string>._, A<string>._,
                A<TimeSpan?>._))
            .ReturnsLazily((string _, Stream stream, string contentType, string fileName, TimeSpan? _) =>
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _cached.Add((copy.ToArray(), contentType, fileName));
                return Task.FromResult("cache-key");
            });
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
        var authorization = A.Fake<IAuthorizationService>();
        A.CallTo(() => authorization.AuthorizeAsync(A<ClaimsPrincipal>._, A<object?>._, A<string>._))
            .Returns(AuthorizationResult.Success());

        _controller = new ModelsController(
            _distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            _importCk,
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            A.Fake<ICatalogService>(),
            ckJsonSerializer,
            _systemContext,
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>(),
            _libraryStatusService,
            NullLogger<ModelsController>.Instance,
            _ckSerializer,
            authorization,
            new CkModelDraftValidator(_compiler, _ckSerializer, _systemContext, _libraryStatusService,
                NullLogger<CkModelDraftValidator>.Instance));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = Tenant;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-4711"), new Claim("role", "DataModelManagement")], "Bearer"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    // ---- helpers ----

    private const string CkModelYaml = "modelId: Demo-1.0.0\ndependencies:\n  - System-[2.0,3.0)\n";
    private const string PumpType = "types:\n  - typeId: Pump\n    derivedFromCkTypeId: System/Entity\n";

    /// <summary>The engine compiler compiles the extracted folder into this model (checks that the folder is complete).</summary>
    private void CompilerReturns(string modelId) =>
        A.CallTo(() => _compiler.CompileInMemoryAsync(A<string>._, A<OperationResult>._))
            .ReturnsLazily((string root, OperationResult _) =>
            {
                File.Exists(Path.Combine(root, "ckModel.yaml")).Should().BeTrue("the extracted folder is the CK root");
                File.Exists(Path.Combine(root, "types", "pump.yaml")).Should().BeTrue();
                _compiledFolders.Add(root);
                return Task.FromResult(new CkCompiledModelRoot
                {
                    ModelId = new CkModelId(modelId), Dependencies = [new CkModelId("System-2.8.0")]
                });
            });

    /// <summary>The engine compiler reports an error in a source file, as it does: message, then CompilerException.</summary>
    private void CompilerFails() =>
        A.CallTo(() => _compiler.CompileInMemoryAsync(A<string>._, A<OperationResult>._))
            .ReturnsLazily<Task<CkCompiledModelRoot>, string, OperationResult>((root, result) =>
            {
                _compiledFolders.Add(root);
                result.AddMessage(new OperationMessage(MessageLevel.Error, Path.Combine(root, "types", "pump.yaml"), 50,
                    $"Base type 'System/Missing' of 'Demo/Pump' not found ({root}).",
                    new Dictionary<string, string> { ["ckTypeId"] = "Demo/Pump", ["attributeName"] = "Flow" }));
                throw new CompilerException(result);
            });

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] SourceZip(params (string Name, string Content)[] extra) =>
        Zip([("ConstructionKit/ckModel.yaml", CkModelYaml), ("ConstructionKit/types/pump.yaml", PumpType), .. extra]);

    private static FormFile Upload(byte[] bytes, string fileName = "draft.zip", string contentType = "application/zip") =>
        new(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };

    private void NothingWasCachedOrQueued()
    {
        A.CallTo(_distributedCache).MustNotHaveHappened();
        A.CallTo(_importCk).MustNotHaveHappened();
    }

    private async Task<CkCompiledModelRoot> ReadCachedModelAsync(int index = 0)
    {
        var (bytes, _, fileName) = _cached[index];
        return await _ckSerializer.DeserializeCompiledModelRootAsync(new MemoryStream(bytes), fileName,
            new OperationResult());
    }

    // ---- source draft: compiled, judged and imported as the compiled model ----

    [Fact]
    public async Task SourceZip_IsCompiled_AndTheCompiledModelIsCachedAndQueued()
    {
        CompilerReturns("Demo-1.0.0");
        var upload = SourceZip();

        var result = await _controller.ImportCk(Upload(upload));

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<TransferModelResponseDto>()
            .Which.JobId.Should().Be("job-1");
        _cached.Should().ContainSingle();
        _cached[0].Bytes.Should().NotEqual(upload, "the job reads the compiled model, never the source zip");
        _cached[0].FileName.Should().EndWith(".json");
        (await ReadCachedModelAsync()).ModelId.FullName.Should().Be("Demo-1.0.0");
        _judged.Should().ContainSingle().Which.Single().ModelId.FullName.Should().Be("Demo-1.0.0");
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(
            A<ImportCkCommandRequest>.That.Matches(r => r.TenantId == Tenant))).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SourceZip_TheExtractedFolderIsDeletedAfterTheCompile()
    {
        CompilerReturns("Demo-1.0.0");

        await _controller.ImportCk(Upload(SourceZip()));

        _compiledFolders.Should().ContainSingle();
        Directory.Exists(_compiledFolders[0]).Should().BeFalse();
    }

    [Fact]
    public async Task SourceZip_IsRecognisedByCkModelYaml_WhateverTheContentTypeAndName()
    {
        // No zip content type, no .zip name: the zip header and ckModel.yaml decide.
        CompilerReturns("Demo-1.0.0");

        var result = await _controller.ImportCk(Upload(SourceZip(), "draft.bin", "application/octet-stream"));

        result.Should().BeOfType<OkObjectResult>();
        (await ReadCachedModelAsync()).ModelId.FullName.Should().Be("Demo-1.0.0");
    }

    [Fact]
    public async Task SourceZip_WithCkModelYamlInASubFolder_IsCompiledFromThatFolder()
    {
        CompilerReturns("Demo-1.0.0");
        var upload = Zip(("my-ck/src/ConstructionKit/ckModel.yaml", CkModelYaml),
            ("my-ck/src/ConstructionKit/types/pump.yaml", PumpType), ("my-ck/README.md", "readme"));

        var result = await _controller.ImportCk(Upload(upload));

        result.Should().BeOfType<OkObjectResult>();
    }

    // ---- compile errors: 400 with the ValidateCk findings, nothing cached or queued ----

    [Fact]
    public async Task SourceZip_WithCompileErrors_IsRefusedWith400_WithTheFindings_AndNothingIsCachedOrQueued()
    {
        CompilerFails();

        var result = await _controller.ImportCk(Upload(SourceZip()));

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        refused.ContentTypes.Should().Contain("application/problem+json");
        var problem = refused.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be(CkModelCompileFailedError.ProblemType);
        problem.Extensions["code"].Should().Be(CkModelCompileFailedError.Code);
        var errors = problem.Extensions["errors"].Should().BeAssignableTo<IEnumerable<CkModelValidationMessageDto>>()
            .Subject.ToList();
        errors.Should().ContainSingle();
        errors[0].MessageNumber.Should().Be(50);
        errors[0].File.Should().Be("types/pump.yaml");
        errors[0].ElementPath.Should().Be("Demo/Pump.Flow");
        problem.Detail.Should().Contain("types/pump.yaml").And.Contain("Nothing was imported");

        NothingWasCachedOrQueued();
        _judged.Should().BeEmpty("the upgrade guard runs only on a compiled model");
    }

    [Fact]
    public async Task SourceZip_WithCompileErrors_NeverShowsAServerPath()
    {
        CompilerFails();

        var result = await _controller.ImportCk(Upload(SourceZip()));

        var json = JsonSerializer.Serialize(((ObjectResult)result).Value);
        json.Should().NotContain("octo-ck-validate-").And.NotContain(Path.GetTempPath().TrimEnd('/', '\\'));
        // The SDK reads a coded refusal from the top-level "code" and "message" members.
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("code").GetString().Should().Be(CkModelCompileFailedError.Code);
        document.RootElement.GetProperty("message").GetString().Should().Contain("does not compile");
    }

    [Fact]
    public async Task SourceZip_WithAnEntryOutsideTheFolder_IsRefusedWith400_AndNothingIsCached()
    {
        var upload = Zip(("ConstructionKit/ckModel.yaml", CkModelYaml), ("ConstructionKit/../../evil.yaml", "x: 1"));

        var result = await _controller.ImportCk(Upload(upload));

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ((ProblemDetails)refused.Value!).Extensions["code"].Should().Be(CkModelCompileFailedError.Code);
        A.CallTo(_compiler).MustNotHaveHappened();
        NothingWasCachedOrQueued();
    }

    // ---- the guards judge the compiled model ----

    [Fact]
    public async Task SourceZip_CompilingToAServiceManagedModel_IsRefused_AndNothingIsCached()
    {
        CompilerReturns("System.Identity-3.7.0");

        var result = await _controller.ImportCk(Upload(SourceZip()));

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ((ProblemDetails)refused.Value!).Extensions["code"].Should().Be(ServiceManagedImportGuard.Code);
        NothingWasCachedOrQueued();
    }

    [Fact]
    public async Task SourceZip_ThatWouldBreakAnInstalledModel_IsRefusedWith409_OnTheCompiledModel()
    {
        CompilerReturns("Demo-2.0.0");
        _blockers = [new CkUpgradeBlockerDto { Dependent = "Plant-1.0.0", Update = "Demo-2.0.0", Message = "breaks" }];

        var result = await _controller.ImportCk(Upload(SourceZip()));

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = (ProblemDetails)refused.Value!;
        problem.Extensions["code"].Should().Be(CkUpgradeBlockedError.Code);
        problem.Extensions["updateSet"].Should().BeEquivalentTo(new[] { "Demo-2.0.0" });
        NothingWasCachedOrQueued();
    }

    [Fact]
    public async Task SourceZip_ThatWouldBreakAnInstalledModel_IsImportedWhenForced()
    {
        CompilerReturns("Demo-2.0.0");
        _blockers = [new CkUpgradeBlockerDto { Dependent = "Plant-1.0.0", Update = "Demo-2.0.0", Message = "breaks" }];

        var result = await _controller.ImportCk(Upload(SourceZip()), forceBreakingUpdate: true);

        result.Should().BeOfType<OkObjectResult>();
        (await ReadCachedModelAsync()).ModelId.FullName.Should().Be("Demo-2.0.0");
    }

    [Fact]
    public async Task SourceZip_WithAnExtraCompiledEntry_ImportsOnlyTheCompiledSource()
    {
        // A stray compiled model in the zip is never what the job imports: only the compiled source is cached.
        CompilerReturns("Demo-1.0.0");
        var upload = SourceZip(("ConstructionKit/out/other.json", "{\"modelId\":\"Other-1.0.0\"}"));

        var result = await _controller.ImportCk(Upload(upload));

        result.Should().BeOfType<OkObjectResult>();
        _cached.Should().ContainSingle();
        (await ReadCachedModelAsync()).ModelId.FullName.Should().Be("Demo-1.0.0");
    }

    // ---- compiled uploads: unchanged ----

    [Fact]
    public async Task CompiledZip_WithoutCkModelYaml_IsNotCompiled_AndTheUploadedBytesAreCached()
    {
        var compiled = "$schema: https://schemas.meshmakers.cloud/construction-kit-compiled.schema.json\n" +
                       "dependencies:\n- System-2.8.0\nmodelId: Demo-1.0.0\n";
        var upload = Zip(("demo.yaml", compiled));

        var result = await _controller.ImportCk(Upload(upload));

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(_compiler).MustNotHaveHappened();
        _cached.Should().ContainSingle().Which.Bytes.Should().Equal(upload);
    }

    [Fact]
    public async Task CompiledYaml_IsNotCompiled_AndTheUploadedBytesAreCached()
    {
        var upload = Encoding.UTF8.GetBytes(
            "$schema: https://schemas.meshmakers.cloud/construction-kit-compiled.schema.json\n" +
            "dependencies:\n- System-2.8.0\nmodelId: Demo-1.0.0\n");

        var result = await _controller.ImportCk(Upload(upload, "demo.yaml", "application/x-yaml"));

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(_compiler).MustNotHaveHappened();
        _cached.Should().ContainSingle().Which.Bytes.Should().Equal(upload);
    }

    [Fact]
    public async Task AFileNamedZipThatIsNoZip_IsReadAsBefore_AndNotCompiled()
    {
        var result = await _controller.ImportCk(Upload(Encoding.UTF8.GetBytes("not a zip"), "draft.zip",
            "application/zip"));

        result.Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(_compiler).MustNotHaveHappened();
        NothingWasCachedOrQueued();
    }
}
