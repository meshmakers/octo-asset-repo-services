using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#7998: ImportFromCatalog / ImportFromCatalogBatch (octo-cli ImportFromCatalog and FixAll) refuse a library version
/// past an installed dependent's range with 409 CK_UPGRADE_BLOCKED naming the dependent, unless forced. The engine guard
/// is faked to report nothing (it treats another major as a parallel install); the real status service decides.
/// </summary>
public class ModelsControllerDependentRangeGuardTests
{
    private const string TenantId = "test-tenant";

    private readonly Dictionary<string, CkCompiledModelRoot> _models = new();
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly ModelsController _controller;
    private readonly ICommandClient<ImportCkCommandRequest> _importClient;
    private readonly ICommandClient<ImportCkBatchCommandRequest> _batchClient;

    public ModelsControllerDependentRangeGuardTests()
    {
        var installed = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => installed.Items).Returns(new[]
        {
            new CkModel { Id = new CkModelId("System-2.8.0"), ModelId = "System", ModelState = ModelState.Available },
            new CkModel
            {
                Id = new CkModelId("Basic.Energy-1.10.0"), ModelId = "Basic.Energy", ModelState = ModelState.Available
            },
            new CkModel
            {
                Id = new CkModelId("EnergyIQ-2.14.0"), ModelId = "EnergyIQ", ModelState = ModelState.Available,
                Dependencies = [new CkModelId("Basic.Energy-1.9.0"), new CkModelId("System-2.7.0")],
                DependencyRanges = [new CkModelDependency { Range = "Basic.Energy-[1.9,2.0)", Floor = "1.9.0" }]
            }
        });
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).Returns(installed);
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .Returns(CkUpgradeGuardResult.None);
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                _models.TryGetValue(id.FullName, out var m)
                    ? Task.FromResult<CkCompiledModelRoot?>(m)
                    : throw ModelCatalogException.ModelNotFound(id, "Test"));
        A.CallTo(() => _catalogService.GetAsync(A<string>._, A<CkModelId>._, A<OperationResult>._,
                A<CancellationToken?>._))
            .ReturnsLazily((string _, CkModelId id, OperationResult _, CancellationToken? _) =>
                Task.FromResult(_models.GetValueOrDefault(id.FullName)));

        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult { TotalCount = 0, SkippedCount = 0, TakeCount = 500, ModelResultItems = [] });

        var distributedCache = A.Fake<IDistributedCacheService>();
        A.CallTo(() => distributedCache.CreateStreamAsync(A<string>._, A<Stream>._, A<string>._, A<string>._,
            A<TimeSpan?>._)).Returns("cache-key");
        var importClient = _importClient = A.Fake<ICommandClient<ImportCkCommandRequest>>();
        A.CallTo(() => importClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
        var batchClient = _batchClient = A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
        A.CallTo(() => batchClient.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._))
            .Returns(new JobCreatedResponse("job-2"));

        _controller = new ModelsController(
            distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            importClient,
            batchClient,
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            systemContext,
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>(),
            new CkModelLibraryStatusService(_catalogService, systemContext),
            A.Fake<ILogger<ModelsController>>(),
            A.Fake<ICkSerializer>(),
            A.Fake<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
            A.Fake<Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation.ICkModelDraftValidator>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        foreach (var id in new[] { "Basic.Energy-1.11.0", "Basic.Energy-2.0.0" })
        {
            _models[id] = new CkCompiledModelRoot { ModelId = new CkModelId(id), Dependencies = [] };
        }
    }

    [Fact]
    public async Task ImportFromCatalog_PastTheDependentsRange_IsRefusedWith409NamingTheDependent()
    {
        var result = await _controller.ImportFromCatalog(
            new ImportFromCatalogRequestDto { CatalogName = "Cat", ModelId = "Basic.Energy-2.0.0" });

        var conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = conflict.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
        var blocker = problem.Extensions["blockers"].Should().BeAssignableTo<IReadOnlyList<CkUpgradeBlockerDto>>()
            .Subject.Should().ContainSingle().Subject;
        blocker.Dependent.Should().Be("EnergyIQ-2.14.0");
        blocker.Subject.Should().Be("Basic.Energy-[1.9,2.0)");
        blocker.Code.Should().Be("CK_UPGRADE_RANGE_NOT_SATISFIED");
        A.CallTo(() => _importClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ImportFromCatalog_PastTheDependentsRange_Forced_IsQueued()
    {
        var result = await _controller.ImportFromCatalog(new ImportFromCatalogRequestDto
            { CatalogName = "Cat", ModelId = "Basic.Energy-2.0.0", ForceBreakingUpdate = true });

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _importClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ImportFromCatalog_InsideTheDependentsRange_IsQueued()
    {
        var result = await _controller.ImportFromCatalog(
            new ImportFromCatalogRequestDto { CatalogName = "Cat", ModelId = "Basic.Energy-1.11.0" });

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_PastTheDependentsRange_IsRefusedWith409()
    {
        var result = await _controller.ImportFromCatalogBatch(
            new ImportFromCatalogBatchRequestDto { CatalogName = "Cat", ModelIds = ["Basic.Energy-2.0.0"] });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _batchClient.GetResponse<JobCreatedResponse>(A<ImportCkBatchCommandRequest>._)).MustNotHaveHappened();
    }
}

