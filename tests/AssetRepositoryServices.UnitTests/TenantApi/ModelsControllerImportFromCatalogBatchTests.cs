using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class ModelsControllerImportFromCatalogBatchTests
{
    private readonly ICatalogService _catalogService;
    private readonly ICkJsonSerializer _ckJsonSerializer;
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatchCommandClient;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly ModelsController _controller;

    public ModelsControllerImportFromCatalogBatchTests()
    {
        _catalogService = A.Fake<ICatalogService>();
        _ckJsonSerializer = A.Fake<ICkJsonSerializer>();
        _distributedCache = A.Fake<IDistributedCacheService>();
        _importCkBatchCommandClient = A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();

        A.CallTo(() => _systemContext.FindTenantContextAsync("test-tenant"))
            .Returns(_tenantContext);

        _controller = new ModelsController(
            _distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            _importCkBatchCommandClient,
            _catalogService,
            _ckJsonSerializer,
            _systemContext,
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task ImportFromCatalogBatch_ReturnsBadRequest_WhenCatalogNameEmpty()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "",
            ModelIds = ["Basic-2.0.0"]
        };

        var result = await _controller.ImportFromCatalogBatch(request);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_ReturnsBadRequest_WhenModelIdsEmpty()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = []
        };

        var result = await _controller.ImportFromCatalogBatch(request);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_ReturnsBadRequest_WhenModelNotInCatalog()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = ["Basic-2.0.0", "NonExistent-1.0.0"]
        };
        SetupCatalogModel("Basic-2.0.0", "Basic", "2.0.0");
        A.CallTo(() => _catalogService.GetAsync(
                "PublicGitHub",
                A<CkModelId>.That.Matches(m => m.FullName == "NonExistent-1.0.0"),
                A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns((CkCompiledModelRoot?)null);

        var result = await _controller.ImportFromCatalogBatch(request);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_SubmitsSingleBatchJob()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = ["Basic-2.0.0", "Energy-1.0.0"]
        };
        SetupCatalogModel("Basic-2.0.0", "Basic", "2.0.0");
        SetupCatalogModel("Energy-1.0.0", "Energy", "1.0.0");
        SetupCacheReturns("cache-key-1", "cache-key-2");
        A.CallTo(() => _importCkBatchCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportCkBatchCommandRequest>.Ignored))
            .Returns(new JobCreatedResponse("batch-job-1"));

        var result = await _controller.ImportFromCatalogBatch(request);

        // Verify single batch job, not multiple individual jobs
        A.CallTo(() => _importCkBatchCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportCkBatchCommandRequest>.Ignored))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ImportFromCatalogBatch_ReturnsOk_WithSingleJobId()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = ["Basic-2.0.0", "Energy-1.0.0"]
        };
        SetupCatalogModel("Basic-2.0.0", "Basic", "2.0.0");
        SetupCatalogModel("Energy-1.0.0", "Energy", "1.0.0");
        SetupCacheReturns("cache-key-1", "cache-key-2");
        A.CallTo(() => _importCkBatchCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportCkBatchCommandRequest>.Ignored))
            .Returns(new JobCreatedResponse("batch-job-1"));

        var result = await _controller.ImportFromCatalogBatch(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BatchImportResponseDto>().Subject;
        response.JobId.Should().Be("batch-job-1");
    }

    [Fact]
    public async Task ImportFromCatalogBatch_CachesAllModelsBeforeSubmitting()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = ["Basic-2.0.0", "Energy-1.0.0", "Environment-3.0.0"]
        };
        SetupCatalogModel("Basic-2.0.0", "Basic", "2.0.0");
        SetupCatalogModel("Energy-1.0.0", "Energy", "1.0.0");
        SetupCatalogModel("Environment-3.0.0", "Environment", "3.0.0");
        SetupCacheReturns("key-1", "key-2", "key-3");
        A.CallTo(() => _importCkBatchCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportCkBatchCommandRequest>.Ignored))
            .Returns(new JobCreatedResponse("job-1"));

        await _controller.ImportFromCatalogBatch(request);

        // All 3 models must be serialized and cached
        A.CallTo(() => _ckJsonSerializer.SerializeAsync(A<StreamWriter>.Ignored, A<CkCompiledModelRoot>.Ignored))
            .MustHaveHappened(3, Times.Exactly);
        A.CallTo(() => _distributedCache.CreateStreamAsync(
                A<string>.Ignored, A<Stream>.Ignored, A<string>.Ignored,
                A<string>.Ignored, A<TimeSpan?>.Ignored))
            .MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task ImportFromCatalogBatch_ReturnsInternalServerError_OnException()
    {
        var request = new ImportFromCatalogBatchRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelIds = ["Basic-2.0.0"]
        };
        A.CallTo(() => _catalogService.GetAsync(
                A<string>.Ignored, A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Throws(new Exception("Catalog unavailable"));

        var result = await _controller.ImportFromCatalogBatch(request);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private void SetupCatalogModel(string fullName, string name, string version)
    {
        var model = new CkCompiledModelRoot
        {
            ModelId = new CkModelId(name, version)
        };
        A.CallTo(() => _catalogService.GetAsync(
                A<string>.Ignored,
                A<CkModelId>.That.Matches(m => m.FullName == fullName),
                A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(model);
    }

    private void SetupCacheReturns(params string[] keys)
    {
        var callIndex = 0;
        A.CallTo(() => _distributedCache.CreateStreamAsync(
                A<string>.Ignored, A<Stream>.Ignored, A<string>.Ignored,
                A<string>.Ignored, A<TimeSpan?>.Ignored))
            .ReturnsLazily(() => keys[callIndex++ % keys.Length]);
    }
}
