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
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class ModelsControllerImportFromCatalogTests
{
    private readonly ICatalogService _catalogService;
    private readonly ICkJsonSerializer _ckJsonSerializer;
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICommandClient<ImportCkCommandRequest> _importCkCommandClient;
    private readonly ModelsController _controller;

    public ModelsControllerImportFromCatalogTests()
    {
        _catalogService = A.Fake<ICatalogService>();
        _ckJsonSerializer = A.Fake<ICkJsonSerializer>();
        _distributedCache = A.Fake<IDistributedCacheService>();
        _importCkCommandClient = A.Fake<ICommandClient<ImportCkCommandRequest>>();

        _controller = new ModelsController(
            _distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            _importCkCommandClient,
            _catalogService,
            _ckJsonSerializer,
            A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelUpgradeService>());

        // Set up HttpContext with tenant ID via request route values
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task ImportFromCatalog_ReturnsBadRequest_WhenCatalogNameEmpty()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "",
            ModelId = "Energy-1.0.0"
        };

        // Act
        var result = await _controller.ImportFromCatalog(request);

        // Assert
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task ImportFromCatalog_ReturnsBadRequest_WhenModelIdEmpty()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = ""
        };

        // Act
        var result = await _controller.ImportFromCatalog(request);

        // Assert
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task ImportFromCatalog_ReturnsNotFound_WhenModelNotInCatalog()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "NonExistent-1.0.0"
        };
        A.CallTo(() => _catalogService.GetAsync(
                "PublicGitHub",
                A<CkModelId>.That.Matches(m => m.FullName == "NonExistent-1.0.0"),
                A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns((CkCompiledModelRoot?)null);

        // Act
        var result = await _controller.ImportFromCatalog(request);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ImportFromCatalog_ReturnsOk_WhenModelFound()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "Energy-2.0.0"
        };
        var compiledModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("Energy", "2.0.0"),
            Description = "Energy model"
        };
        A.CallTo(() => _catalogService.GetAsync(
                "PublicGitHub",
                A<CkModelId>.Ignored,
                A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(compiledModel);
        A.CallTo(() => _distributedCache.CreateStreamAsync(
                A<string>.Ignored, A<Stream>.Ignored, A<string>.Ignored, A<string>.Ignored,
                A<TimeSpan?>.Ignored))
            .Returns("cache-key-123");
        A.CallTo(() => _importCkCommandClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>.Ignored))
            .Returns(new JobCreatedResponse("job-456"));

        // Act
        var result = await _controller.ImportFromCatalog(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<TransferModelResponseDto>().Subject;
        response.JobId.Should().Be("job-456");
    }

    [Fact]
    public async Task ImportFromCatalog_SerializesModelAndCachesIt()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "LocalFileSystem",
            ModelId = "System-1.0.0"
        };
        var compiledModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("System", "1.0.0")
        };
        A.CallTo(() => _catalogService.GetAsync(
                A<string>.Ignored, A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(compiledModel);
        A.CallTo(() => _distributedCache.CreateStreamAsync(
                A<string>.Ignored, A<Stream>.Ignored, A<string>.Ignored, A<string>.Ignored,
                A<TimeSpan?>.Ignored))
            .Returns("key");
        A.CallTo(() => _importCkCommandClient.GetResponse<JobCreatedResponse>(A<ImportCkCommandRequest>.Ignored))
            .Returns(new JobCreatedResponse("job"));

        // Act
        await _controller.ImportFromCatalog(request);

        // Assert - verify serialization was called
        A.CallTo(() => _ckJsonSerializer.SerializeAsync(A<StreamWriter>.Ignored, compiledModel))
            .MustHaveHappenedOnceExactly();

        // Assert - verify cache was called with correct content type
        A.CallTo(() => _distributedCache.CreateStreamAsync(
                A<string>.Ignored, A<Stream>.Ignored, "application/json",
                "System-1.0.0.json", A<TimeSpan?>.Ignored))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ImportFromCatalog_ReturnsInternalServerError_OnException()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "Energy-1.0.0"
        };
        A.CallTo(() => _catalogService.GetAsync(
                A<string>.Ignored, A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Throws(new Exception("Catalog unavailable"));

        // Act
        var result = await _controller.ImportFromCatalog(request);

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
