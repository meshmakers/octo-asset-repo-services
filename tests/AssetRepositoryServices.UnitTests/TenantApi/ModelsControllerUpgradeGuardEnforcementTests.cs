using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#6870: ImportFromCatalog and ImportFromCatalogBatch enforce the tenant upgrade guard (AB#6867). A refused update
/// set is answered with 409 CK_UPGRADE_BLOCKED (problem details with the structured blockers) before anything is
/// cached, queued or written - the batch is refused as a whole (no partial import). ForceBreakingUpdate imports anyway
/// and leaves a WARN audit line with the blockers and the caller. The scenario is the staging-1 tenant sbeg:
/// Registration 2.8.0 is exact-pinned to Basic-2.4.0 / Basic.Energy-1.9.0.
/// </summary>
public class ModelsControllerUpgradeGuardEnforcementTests
{
    private const string Registration210 = "EnergyCommunity.Registration-2.10.0";

    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly IDistributedCacheService _distributedCache = A.Fake<IDistributedCacheService>();
    private readonly ICommandClient<ImportCkCommandRequest> _importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatch =
        A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
    private readonly ICkModelLibraryStatusService _libraryStatusService = A.Fake<ICkModelLibraryStatusService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ListLogger _logger = new();
    private readonly ModelsController _controller;
    private readonly List<List<string>> _updateSetsJudged = [];

    public ModelsControllerUpgradeGuardEnforcementTests()
    {
        A.CallTo(() => _systemContext.FindTenantContextAsync("sbeg")).Returns(_tenantContext);
        A.CallTo(() => _libraryStatusService.GetInstalledSystemVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion>());
        A.CallTo(() => _libraryStatusService.CheckSystemCompatibilityAsync(
                A<CkModelId>._, A<Dictionary<string, CkVersion>>._, A<HashSet<string>>._,
                A<List<string>>._, A<CancellationToken>._))
            .Returns((true, (string?)null));

        A.CallTo(() => _catalogService.GetAsync(A<string>._, A<CkModelId>._, A<OperationResult>._, A<CancellationToken?>._))
            .ReturnsLazily((string _, CkModelId id, OperationResult _, CancellationToken? _) =>
                Task.FromResult<CkCompiledModelRoot?>(new CkCompiledModelRoot { ModelId = id }));
        A.CallTo(() => _distributedCache.CreateStreamAsync(A<string>._, A<Stream>._, A<string>._, A<string>._,
            A<TimeSpan?>._)).Returns("cache-key");
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
        A.CallTo(() => _importCkBatch.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .Returns(new JobCreatedResponse("batch-1"));

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
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "sbeg";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-4711"), new Claim("name", "Ada Admin")], "Bearer"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static CkUpgradeBlockerDto Blocker(string update, string pin, string? hint = null) => new()
    {
        Dependent = "EnergyCommunity.Registration-2.8.0", Update = update, Kind = "ExactPin", Subject = pin,
        Code = "CK_UPGRADE_EXACT_PIN",
        Message = $"EnergyCommunity.Registration 2.8.0 would break: exact pin {pin}: {update} replaces it",
        SuggestedDependentUpdate = hint == null ? null : Registration210, ResolveHint = hint
    };

    /// <summary>The faked guard: every set without Registration 2.10.0 breaks Registration 2.8.0 (one blocker per
    /// update), a set with it is clear (it is range-retaining).</summary>
    private void FakeGuardSbeg()
    {
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync("sbeg", A<IReadOnlyList<CkModelId>>._,
                A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<CkModelId> set, CancellationToken _) =>
            {
                var ids = set.Select(i => i.FullName).ToList();
                _updateSetsJudged.Add(ids);
                IReadOnlyList<CkUpgradeBlockerDto> blockers = ids.Contains(Registration210)
                    ? []
                    : ids.Where(i => !i.StartsWith("EnergyCommunity.Registration", StringComparison.Ordinal))
                        .Select(i => Blocker(i, i.Split('-')[0] + "-old", "update EnergyCommunity.Registration to 2.10.0 first"))
                        .ToList();
                return Task.FromResult(blockers);
            });
    }

    private void FakeGuardClear() =>
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync("sbeg", A<IReadOnlyList<CkModelId>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkUpgradeBlockerDto>>([]));

    private void NothingMayHaveBeenWritten()
    {
        A.CallTo(_distributedCache).MustNotHaveHappened();
        A.CallTo(_importCk).MustNotHaveHappened();
        A.CallTo(_importCkBatch).MustNotHaveHappened();
        A.CallTo(_tenantContext).MustNotHaveHappened();
    }

    private static ImportFromCatalogRequestDto Single(bool force = false) =>
        new() { CatalogName = "Test", ModelId = "Basic-2.5.0", ForceBreakingUpdate = force };

    private static ImportFromCatalogBatchRequestDto Batch(bool force, params string[] ids) =>
        new() { CatalogName = "Test", ModelIds = ids.ToList(), ForceBreakingUpdate = force };

    // ---- ImportFromCatalog ----

    [Fact]
    public async Task ImportFromCatalog_SbegCase_IsRefusedWith409_AndNothingIsCachedQueuedOrWritten()
    {
        FakeGuardSbeg();

        var result = await _controller.ImportFromCatalog(Single());

        var conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.ContentTypes.Should().Contain("application/problem+json");
        var problem = conflict.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(409);
        problem.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
        problem.Extensions["updateSet"].Should().BeEquivalentTo(new[] { "Basic-2.5.0" });
        var blockers = problem.Extensions["blockers"].Should().BeAssignableTo<IReadOnlyList<CkUpgradeBlockerDto>>().Subject;
        blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        problem.Detail.Should().Contain("EnergyCommunity.Registration 2.8.0 would break")
            .And.Contain("update EnergyCommunity.Registration to 2.10.0 first")
            .And.Contain("Nothing was imported");
        _updateSetsJudged.Should().ContainSingle().Which.Should().Equal("Basic-2.5.0");
        NothingMayHaveBeenWritten();
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportFromCatalog_AdditiveUpdate_StillImports()
    {
        FakeGuardClear();

        var result = await _controller.ImportFromCatalog(Single());

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<TransferModelResponseDto>()
            .Which.JobId.Should().Be("job-1");
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .MustHaveHappenedOnceExactly();
        _logger.Entries.Should().BeEmpty("an unforced, unblocked import needs no audit line");
    }

    [Fact]
    public async Task ImportFromCatalog_Forced_QueuesTheJob_AndLogsTheBlockersAndTheCallerAtWarn()
    {
        FakeGuardSbeg();

        var result = await _controller.ImportFromCatalog(Single(force: true));

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _importCk.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .MustHaveHappenedOnceExactly();
        var warn = _logger.Entries.Should().ContainSingle().Subject;
        warn.Level.Should().Be(LogLevel.Warning);
        warn.Message.Should().Contain("sbeg").And.Contain("sub=user-4711").And.Contain("name=Ada Admin")
            .And.Contain("Basic-2.5.0").And.Contain("EnergyCommunity.Registration-2.8.0").And.Contain("ExactPin");
    }

    [Fact]
    public async Task ImportFromCatalog_ForcedWithoutBlockers_NeedsNoAuditLine()
    {
        FakeGuardClear();

        var result = await _controller.ImportFromCatalog(Single(force: true));

        result.Should().BeOfType<OkObjectResult>();
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportFromCatalog_GuardFailure_FailsClosed_With500_AndWritesNothing()
    {
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync("sbeg", A<IReadOnlyList<CkModelId>>._,
                A<CancellationToken>._))
            .ThrowsAsync(new Exception("guard exploded"));

        var result = await _controller.ImportFromCatalog(Single(force: true));

        // Even a forced import does not proceed on a guard failure: the blockers (and the audit line) are unknown.
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        NothingMayHaveBeenWritten();
    }

    // ---- ImportFromCatalogBatch ----

    [Fact]
    public async Task Batch_RegistrationInTheSet_IsJudgedTogether_AndAcceptedWithoutForce()
    {
        FakeGuardSbeg();

        var result = await _controller.ImportFromCatalogBatch(Batch(false, Registration210, "Basic-2.5.0",
            "Basic.Energy-1.10.0", "EnergyCommunity-4.10.0"));

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<BatchImportResponseDto>()
            .Which.JobId.Should().Be("batch-1");
        _updateSetsJudged.Should().ContainSingle().Which.Should().BeEquivalentTo(new[]
            { Registration210, "Basic-2.5.0", "Basic.Energy-1.10.0", "EnergyCommunity-4.10.0" });
        A.CallTo(() => _importCkBatch.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Batch_WithoutRegistration_IsRefusedAsAWhole_NoPartialImport()
    {
        FakeGuardSbeg();

        var result = await _controller.ImportFromCatalogBatch(Batch(false, "Basic-2.5.0", "Basic.Energy-1.10.0",
            "EnergyCommunity-4.10.0"));

        var conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        var problem = conflict.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
        problem.Extensions["updateSet"].Should().BeEquivalentTo(new[]
            { "Basic-2.5.0", "Basic.Energy-1.10.0", "EnergyCommunity-4.10.0" });
        problem.Extensions["blockers"].Should().BeAssignableTo<IReadOnlyList<CkUpgradeBlockerDto>>().Subject
            .Should().HaveCount(3);
        // All-or-nothing: not even the unblocked members were cached, queued or imported.
        NothingMayHaveBeenWritten();
    }

    [Fact]
    public async Task Batch_OneBlockedModel_RefusesTheWholeBatch()
    {
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync("sbeg", A<IReadOnlyList<CkModelId>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkUpgradeBlockerDto>>([Blocker("Basic-2.5.0", "Basic-2.4.0")]));

        var result = await _controller.ImportFromCatalogBatch(Batch(false, "Basic-2.5.0", "Unrelated-1.0.0"));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        NothingMayHaveBeenWritten();
    }

    [Fact]
    public async Task Batch_Forced_QueuesTheSingleBatchJob_AndLogsTheBlockersAndTheCallerAtWarn()
    {
        FakeGuardSbeg();

        var result = await _controller.ImportFromCatalogBatch(Batch(true, "Basic-2.5.0", "Basic.Energy-1.10.0"));

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _importCkBatch.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .MustHaveHappenedOnceExactly();
        var warn = _logger.Entries.Should().ContainSingle().Subject;
        warn.Level.Should().Be(LogLevel.Warning);
        warn.Message.Should().Contain("sub=user-4711").And.Contain("Basic-2.5.0, Basic.Energy-1.10.0")
            .And.Contain("breaks 2 installed model(s)");
    }

    [Fact]
    public async Task Batch_UnknownModel_IsStillABadRequest_BeforeTheGuardRuns()
    {
        A.CallTo(() => _catalogService.GetAsync("Test", A<CkModelId>.That.Matches(m => m.Name == "Missing"),
                A<OperationResult>._, A<CancellationToken?>._))
            .Returns(Task.FromResult<CkCompiledModelRoot?>(null));

        var result = await _controller.ImportFromCatalogBatch(Batch(false, "Basic-2.5.0", "Missing-1.0.0"));

        result.Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(() => _libraryStatusService.EvaluateUpdateSetBlockersAsync(A<string>._, A<IReadOnlyList<CkModelId>>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    // ---- Authorization, additive DTOs ----

    [Theory]
    [InlineData(nameof(ModelsController.ImportFromCatalog))]
    [InlineData(nameof(ModelsController.ImportFromCatalogBatch))]
    public void BothImportEndpoints_RequireTheDataModelManagementPolicy_SoForceIsRejectedWithoutIt(string action)
    {
        // The force flag travels in the request body of an endpoint that is already gated: a caller without
        // DataModelManagementPolicy never reaches the controller (403), with or without the flag.
        var method = typeof(ModelsController).GetMethod(action)!;

        method.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("DataModelManagementPolicy");
    }

    [Fact]
    public void RequestDtos_AreAdditive_OldPayloadsDeserializeToNoForce()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        JsonSerializer.Deserialize<ImportFromCatalogRequestDto>("""{"catalogName":"c","modelId":"Basic-2.5.0"}""", web)!
            .ForceBreakingUpdate.Should().BeFalse();
        JsonSerializer.Deserialize<ImportFromCatalogBatchRequestDto>("""{"catalogName":"c","modelIds":["a-1.0.0"]}""", web)!
            .ForceBreakingUpdate.Should().BeFalse();
        JsonSerializer.Deserialize<ImportFromCatalogRequestDto>(
                """{"catalogName":"c","modelId":"Basic-2.5.0","forceBreakingUpdate":true}""", web)!
            .ForceBreakingUpdate.Should().BeTrue();
    }

    [Fact]
    public void ResolveResponses_KeepTheirPreviousShape_AndAddIsBlockedAndBlockers()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        using var batch = JsonDocument.Parse(JsonSerializer.Serialize(new BatchDependencyResolutionResponseDto(), web));
        batch.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .Contain(["modelsToImport", "dependencyTrees", "isBlocked", "blockers"]);
        using var tree = JsonDocument.Parse(JsonSerializer.Serialize(new DependencyResolutionResponseDto
            { RootModel = new DependencyResolutionItemDto() }, web));
        tree.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(["rootModel", "isBlocked", "blockers"]);

        // A previous payload without the members still deserializes (old SDK clients ignore the new ones).
        JsonSerializer.Deserialize<BatchDependencyResolutionResponseDto>("""{"modelsToImport":["a-1.0.0"],"dependencyTrees":[]}""", web)!
            .IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void Problem_IsLanguageNeutral_CodeUpdateSetAndStructuredBlockers()
    {
        var problem = CkUpgradeBlockedError.CreateProblem(["Basic-2.5.0"],
            [Blocker("Basic-2.5.0", "Basic-2.4.0", "update EnergyCommunity.Registration to 2.10.0 first")]);

        problem.Type.Should().Be("https://schemas.meshmakers.cloud/errors/ck-upgrade-blocked");
        problem.Status.Should().Be(409);
        problem.Extensions["code"].Should().Be(CkUpgradeBlockedError.Code).And.Be("CK_UPGRADE_BLOCKED");
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
