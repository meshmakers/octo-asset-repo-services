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
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     AB#9293 (Security): caller-initiated CK imports refuse every service-managed model (<c>System</c>,
///     <c>System.*</c>), whatever the version and whoever calls. Invariant: no request to <c>ImportCk</c>,
///     <c>ImportFromCatalog</c> or <c>ImportFromCatalogBatch</c> caches, queues or imports such a model; the refusal is
///     decided on the model the bytes parse to (the real CK serializer, the one bot-services imports with), before
///     anything is cached or queued. One test per abuse case of the work item.
/// </summary>
public class ModelsControllerServiceManagedImportTests
{
    private const string Tenant = "acme";

    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly IDistributedCacheService _distributedCache = A.Fake<IDistributedCacheService>();
    private readonly ICommandClient<ImportCkCommandRequest> _importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatch =
        A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
    private readonly ICkModelLibraryStatusService _libraryStatusService = A.Fake<ICkModelLibraryStatusService>();
    private readonly IAuthorizationService _authorization = A.Fake<IAuthorizationService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ListLogger _logger = new();
    private readonly ICkSerializer _ckSerializer;
    private readonly ModelsController _controller;
    private readonly List<byte[]> _cachedUploads = [];

    public ModelsControllerServiceManagedImportTests()
    {
        // The real serializer: the refusal must judge what bot-services' import job will parse (same ICkSerializer).
        _ckSerializer = new ServiceCollection().AddLogging().AddConstructionKit().BuildServiceProvider()
            .GetRequiredService<ICkSerializer>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(Tenant)).Returns(_tenantContext);
        A.CallTo(() => _libraryStatusService.GetInstalledSystemVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion>());
        A.CallTo(() => _libraryStatusService.CheckSystemCompatibilityAsync(
                A<CkModelId>._, A<Dictionary<string, CkVersion>>._, A<HashSet<string>>._,
                A<List<string>>._, A<CancellationToken>._))
            .Returns((true, (string?)null));
        A.CallTo(() => _libraryStatusService.EvaluateFileImportBlockersAsync(A<string>._,
                A<IReadOnlyList<CkCompiledModelRoot>>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkUpgradeBlockerDto>>([]));
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync(A<string>._, A<IReadOnlyList<CkModelId>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkUpgradeBlockerDto>>([]));
        A.CallTo(() => _catalogService.GetAsync(A<string>._, A<CkModelId>._, A<OperationResult>._, A<CancellationToken?>._))
            .ReturnsLazily((string _, CkModelId id, OperationResult _, CancellationToken? _) =>
                Task.FromResult<CkCompiledModelRoot?>(new CkCompiledModelRoot { ModelId = id }));
        A.CallTo(() => _distributedCache.CreateStreamAsync(A<string>._, A<Stream>._, A<string>._, A<string>._,
                A<TimeSpan?>._))
            .ReturnsLazily((string _, Stream stream, string _, string _, TimeSpan? _) =>
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _cachedUploads.Add(copy.ToArray());
                return Task.FromResult("cache-key");
            });
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
        A.CallTo(() => _importCkBatch.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .Returns(new JobCreatedResponse("batch-1"));
        A.CallTo(() => _authorization.AuthorizeAsync(A<ClaimsPrincipal>._, A<object?>._, A<string>._))
            .Returns(AuthorizationResult.Success());

        _controller = new ModelsController(
            _distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            _importCk,
            _importCkBatch,
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            _systemContext,
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>(),
            _libraryStatusService,
            _logger,
            _ckSerializer,
            _authorization,
            A.Fake<ICkModelDraftValidator>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = Tenant;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-4711"), new Claim("role", "DataModelManagement")], "Bearer"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    // ---- helpers ----

    /// <summary>A compiled CK model as the CK compiler writes it (YAML), parsed by the real serializer.</summary>
    private static string CompiledModel(string modelId, string? description = null) =>
        "$schema: https://schemas.meshmakers.cloud/construction-kit-compiled.schema.json\n" +
        "dependencies:\n- System-2.8.0\n" +
        (description == null ? "" : $"description: {description}\n") +
        $"modelId: {modelId}\n";

    private static FormFile Upload(byte[] bytes, string fileName, string contentType) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };

    private static FormFile YamlUpload(string modelId, string fileName = "model.yaml",
        string contentType = "application/x-yaml", string? description = null) =>
        Upload(Encoding.UTF8.GetBytes(CompiledModel(modelId, description)), fileName, contentType);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private void NothingMayHaveBeenCachedQueuedOrWritten()
    {
        A.CallTo(_distributedCache).MustNotHaveHappened();
        A.CallTo(_importCk).MustNotHaveHappened();
        A.CallTo(_importCkBatch).MustNotHaveHappened();
        A.CallTo(_tenantContext).MustNotHaveHappened();
        A.CallTo(() => _libraryStatusService.EvaluateFileImportBlockersAsync(A<string>._,
            A<IReadOnlyList<CkCompiledModelRoot>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static void ShouldBeTheRefusal(IActionResult result, params string[] models)
    {
        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = refused.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be(ServiceManagedImportGuard.ProblemType);
        problem.Extensions["code"].Should().Be(ServiceManagedImportGuard.Code);
        problem.Extensions["models"].Should().BeEquivalentTo(models);
        problem.Detail.Should().Contain("Nothing was imported").And.Contain("ClearCache");

        // The SDK reads a coded refusal from the top-level "code" and "message" members (AB#8529).
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(problem));
        json.RootElement.GetProperty("code").GetString().Should().Be(ServiceManagedImportGuard.Code);
        json.RootElement.GetProperty("message").GetString().Should().Contain(models[0]);
    }

    // ---- abuse case 1-3: System.Identity upgrade, downgrade, same version ----

    [Fact]
    public async Task ImportCk_ForgedHigherMinorOfSystemIdentity_IsRefused_AndNothingIsCachedOrQueued()
    {
        // The attack of the bug: a self-built 3.7.0 without the writeAccess restriction of 3.6.0 (AB#9291).
        var result = await _controller.ImportCk(YamlUpload("System.Identity-3.7.0"));

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
        _logger.Entries.Should().Contain(e => e.Message.Contains("System.Identity-3.7.0") &&
                                              e.Message.Contains(Tenant));
    }

    [Fact]
    public async Task ImportCk_ExplicitDowngradeOfSystemIdentity_IsRefused()
    {
        var result = await _controller.ImportCk(YamlUpload("System.Identity-3.5.0"));

        ShouldBeTheRefusal(result, "System.Identity-3.5.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_SameVersionWithChangedContent_IsRefused()
    {
        var result = await _controller.ImportCk(YamlUpload("System.Identity-3.6.0",
            description: "rebuilt without the write channel restriction"));

        ShouldBeTheRefusal(result, "System.Identity-3.6.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    // ---- abuse case 4: other service-managed models ----

    [Theory]
    [InlineData("System-2.9.0")]
    [InlineData("System.StreamData-1.30.0")]
    [InlineData("System.Communication-4.2.0")]
    [InlineData("System.Bot-3.9.0")]
    [InlineData("System.UI-2.9.0")]
    public async Task ImportCk_EveryServiceManagedModel_IsRefused(string modelId)
    {
        // One platform-wide name rule, not a per-service list: System.Identity and System.Communication are owned by
        // other services than the asset repository and are refused all the same.
        var result = await _controller.ImportCk(YamlUpload(modelId));

        ShouldBeTheRefusal(result, modelId);
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    // ---- abuse case 5: casing and alias tricks ----

    [Theory]
    [InlineData("system.identity-3.7.0")]
    [InlineData("SYSTEM.Identity-3.7.0")]
    [InlineData("System.IDENTITY-3.7.0")]
    [InlineData("system-2.9.0")]
    public async Task ImportCk_ServiceManagedNameInAnyCasing_IsRefused(string modelId)
    {
        var result = await _controller.ImportCk(YamlUpload(modelId));

        ShouldBeTheRefusal(result, modelId);
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_HarmlessFileNameAndContentTypeWithSystemContent_IsRefusedByContent()
    {
        var result = await _controller.ImportCk(YamlUpload("System.Identity-3.7.0", "basic-2.6.0.json",
            "application/json"));

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    // ---- abuse case 7: forceBreakingUpdate ----

    [Fact]
    public async Task ImportCk_ForceBreakingUpdate_DoesNotBypassTheRefusal()
    {
        var result = await _controller.ImportCk(YamlUpload("System.Identity-3.7.0"), forceBreakingUpdate: true);

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    // ---- abuse case 8: parser differential (zip entry choice) ----

    [Theory]
    [InlineData("a-basic.yaml", "b-system.yaml")]
    [InlineData("a-basic.json", "b-system.yaml")]
    [InlineData("a-basic.yaml", "b-system.YAML")]
    [InlineData("a-basic.yaml", "b-system.yml")]
    [InlineData("a-basic.json", "b-system.Json")]
    public async Task ImportCk_ZipWithAServiceManagedModelInAnyEntry_IsRefused(string harmlessEntry, string systemEntry)
    {
        // This reader picks the first .json (else .yaml) entry, bot-services the first .yaml entry when the zip holds
        // no .json: the refusal looks at every model entry, so no entry choice can import what it did not see.
        var zip = Zip((harmlessEntry, CompiledModel("Basic-2.6.0")), (systemEntry, CompiledModel("System.Identity-3.7.0")));

        var result = await _controller.ImportCk(Upload(zip, "models.zip", "application/zip"));

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_ZipWithAServiceManagedModelAsTheChosenEntry_IsRefused()
    {
        var zip = Zip(("system.yaml", CompiledModel("System.Identity-3.7.0")), ("readme.txt", "not a model"));

        var result = await _controller.ImportCk(Upload(zip, "models.zip", "application/x-zip-compressed"));

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_ZipWhoseEntriesUnpackBeyondTheLimit_IsRefusedWith400_AndNothingIsCached()
    {
        // The scan unpacks every model entry: a zip bomb in a later entry must fail closed, not fill the memory.
        var zip = Zip(("a-basic.yaml", CompiledModel("Basic-2.6.0")),
            ("b-big.yaml", new string(' ', (int)CkModelDraftValidator.MaxUnpackedBytes + 1)));

        var result = await _controller.ImportCk(Upload(zip, "models.zip", "application/zip"));

        result.Should().BeOfType<BadRequestObjectResult>();
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_ZipWithTooManyEntries_IsRefusedWith400_AndNothingIsCached()
    {
        var entries = Enumerable.Range(0, CkModelDraftValidator.MaxZipEntries + 1)
            .Select(i => ($"m{i:D4}.txt", "x")).Prepend(("a-basic.yaml", CompiledModel("Basic-2.6.0"))).ToArray();

        var result = await _controller.ImportCk(Upload(Zip(entries), "models.zip", "application/zip"));

        result.Should().BeOfType<BadRequestObjectResult>();
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportCk_HarmlessFile_IsQueued_AndTheCachedBytesAreExactlyTheJudgedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes(CompiledModel("Basic-2.6.0"));

        var result = await _controller.ImportCk(Upload(bytes, "basic.yaml", "application/x-yaml"));

        result.Should().BeOfType<OkObjectResult>();
        _cachedUploads.Should().ContainSingle().Which.Should().Equal(bytes);
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ImportCk_HarmlessZip_IsQueued_WithTheUploadedBytes()
    {
        var zip = Zip(("basic.yaml", CompiledModel("Basic-2.6.0")), ("energy.yaml", CompiledModel("Basic.Energy-1.9.0")));

        var result = await _controller.ImportCk(Upload(zip, "models.zip", "application/zip"));

        result.Should().BeOfType<OkObjectResult>();
        _cachedUploads.Should().ContainSingle().Which.Should().Equal(zip);
    }

    // ---- abuse case 6: other transports ----

    [Theory]
    [InlineData("System.Identity-3.5.0")]
    [InlineData("system.identity-3.7.0")]
    [InlineData("System.Communication-4.2.0")]
    [InlineData("System.StreamData-1.30.0")]
    public async Task ImportFromCatalog_ServiceManagedModel_IsRefused_BeforeTheCatalogIsRead(string modelId)
    {
        var result = await _controller.ImportFromCatalog(new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub", ModelId = modelId, ForceBreakingUpdate = true
        });

        ShouldBeTheRefusal(result, modelId);
        NothingMayHaveBeenCachedQueuedOrWritten();
        A.CallTo(_catalogService).MustNotHaveHappened();
    }

    [Fact]
    public async Task ImportFromCatalog_HarmlessIdWhoseCatalogContentIsServiceManaged_IsRefusedByContent()
    {
        A.CallTo(() => _catalogService.GetAsync(A<string>._, A<CkModelId>._, A<OperationResult>._, A<CancellationToken?>._))
            .Returns(Task.FromResult<CkCompiledModelRoot?>(
                new CkCompiledModelRoot { ModelId = new CkModelId("System.Identity-3.7.0") }));

        var result = await _controller.ImportFromCatalog(new ImportFromCatalogRequestDto
        {
            CatalogName = "LocalFileSystem", ModelId = "Basic-2.6.0"
        });

        ShouldBeTheRefusal(result, "System.Identity-3.7.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_OneServiceManagedModel_RefusesTheWholeBatch_BeforeAnythingIsLoaded()
    {
        var result = await _controller.ImportFromCatalogBatch(new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub", ModelIds = ["Basic-2.6.0", "SYSTEM.Identity-3.5.0", "Basic.Energy-1.9.0"]
        });

        ShouldBeTheRefusal(result, "SYSTEM.Identity-3.5.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
        A.CallTo(_catalogService).MustNotHaveHappened();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_HarmlessIdsWhoseCatalogContentIsServiceManaged_RefusesTheWholeBatch()
    {
        A.CallTo(() => _catalogService.GetAsync(A<string>._, A<CkModelId>.That.Matches(id => id.Name == "Basic.Energy"),
                A<OperationResult>._, A<CancellationToken?>._))
            .Returns(Task.FromResult<CkCompiledModelRoot?>(
                new CkCompiledModelRoot { ModelId = new CkModelId("System.Communication-4.2.0") }));

        var result = await _controller.ImportFromCatalogBatch(new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "LocalFileSystem", ModelIds = ["Basic-2.6.0", "Basic.Energy-1.9.0"]
        });

        ShouldBeTheRefusal(result, "System.Communication-4.2.0");
        NothingMayHaveBeenCachedQueuedOrWritten();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_HarmlessModels_AreStillQueued()
    {
        var result = await _controller.ImportFromCatalogBatch(new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub", ModelIds = ["Basic-2.6.0", "Basic.Energy-1.9.0"]
        });

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _importCkBatch.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    // ---- the name rule ----

    [Theory]
    [InlineData("System", true)]
    [InlineData("System.Identity", true)]
    [InlineData("system.identity", true)]
    [InlineData("SYSTEM", true)]
    [InlineData(" System.Identity ", true)]
    [InlineData("System.Communication", true)]
    [InlineData("Systemx", false)]
    [InlineData("SystemIdentity", false)]
    [InlineData("MySystem.Identity", false)]
    [InlineData("Basic", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsServiceManaged_IsOnePlatformWideNameRule(string? name, bool expected)
    {
        ServiceManagedImportGuard.IsServiceManaged(name).Should().Be(expected);
        if (name != null)
        {
            CkModelLibraryStatusService.IsSystemManaged(name).Should().Be(expected);
        }
    }

    private sealed class ListLogger : ILogger<ModelsController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
