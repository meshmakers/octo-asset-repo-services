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
/// AB#7940: ImportFromCatalog and ImportFromCatalogBatch run the real <see cref="CkModelLibraryStatusService" />
/// System-compatibility check. A range-retaining model that reaches System only transitively (EnergyIQ via Basic)
/// must import against a newer installed System; an exact-pinned intermediate keeps blocking.
/// </summary>
public class ModelsControllerImportTransitiveSystemPinTests
{
    private const string TenantId = "test-tenant";

    private readonly Dictionary<string, CkCompiledModelRoot> _models = new();
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly ModelsController _controller;

    public ModelsControllerImportTransitiveSystemPinTests()
    {
        var installed = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => installed.Items).Returns(new[]
        {
            new CkModel { Id = new CkModelId("System-2.8.0"), ModelId = "System", ModelState = ModelState.Available }
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
        var importClient = A.Fake<ICommandClient<ImportCkCommandRequest>>();
        A.CallTo(() => importClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
        var batchClient = A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
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
            A.Fake<Microsoft.AspNetCore.Authorization.IAuthorizationService>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private void Seed(string intermediateExactSystemPin = null!)
    {
        _models["Basic-2.5.0"] = intermediateExactSystemPin == null
            ? new CkCompiledModelRoot
            {
                ModelId = new CkModelId("Basic-2.5.0"),
                Dependencies = [new CkModelId("System-2.5.0")],
                DependencyRanges =
                [
                    new CkModelDependencyDto
                        { Range = new CkModelIdVersionRange("System", "[2.5,3.0)"), Floor = "2.5.0" }
                ]
            }
            : new CkCompiledModelRoot
            {
                ModelId = new CkModelId("Basic-2.5.0"),
                Dependencies = [new CkModelId(intermediateExactSystemPin)]
            };
        _models["EnergyIQ-2.14.0"] = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("EnergyIQ-2.14.0"),
            Dependencies = [new CkModelId("Basic-2.5.0"), new CkModelId("System-2.7.0")],
            DependencyRanges =
            [
                new CkModelDependencyDto { Range = new CkModelIdVersionRange("Basic", "[2.5,3.0)"), Floor = "2.5.0" }
            ]
        };
    }

    [Fact]
    public async Task ImportFromCatalog_TransitiveSystemViaRangeRetainingIntermediate_Succeeds()
    {
        Seed();

        var result = await _controller.ImportFromCatalog(
            new ImportFromCatalogRequestDto { CatalogName = "Cat", ModelId = "EnergyIQ-2.14.0" });

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ImportFromCatalog_TransitiveSystemViaExactPinnedIntermediate_IsBlocked()
    {
        Seed("System-2.7.0");

        var result = await _controller.ImportFromCatalog(
            new ImportFromCatalogRequestDto { CatalogName = "Cat", ModelId = "EnergyIQ-2.14.0" });

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<OperationFailedErrorDto>().Which.Message.Should()
            .Contain("Import blocked").And.Contain("Requires System-2.7.0, but System-2.8.0 is installed");
    }

    [Fact]
    public async Task ImportFromCatalogBatch_TransitiveSystemViaRangeRetainingIntermediate_Succeeds()
    {
        Seed();

        var result = await _controller.ImportFromCatalogBatch(
            new ImportFromCatalogBatchRequestDto { CatalogName = "Cat", ModelIds = ["EnergyIQ-2.14.0"] });

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_TransitiveSystemViaExactPinnedIntermediate_IsBlocked()
    {
        Seed("System-2.7.0");

        var result = await _controller.ImportFromCatalogBatch(
            new ImportFromCatalogBatchRequestDto { CatalogName = "Cat", ModelIds = ["EnergyIQ-2.14.0"] });

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<OperationFailedErrorDto>().Which.Message.Should()
            .Contain("Import blocked for 'EnergyIQ-2.14.0'");
    }
}
