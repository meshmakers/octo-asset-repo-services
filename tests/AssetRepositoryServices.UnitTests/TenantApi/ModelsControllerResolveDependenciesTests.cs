using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class ModelsControllerResolveDependenciesTests
{
    private readonly ICatalogService _catalogService;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly ModelsController _controller;

    public ModelsControllerResolveDependenciesTests()
    {
        _catalogService = A.Fake<ICatalogService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();

        A.CallTo(() => _systemContext.FindTenantContextAsync("test-tenant"))
            .Returns(_tenantContext);

        _controller = new ModelsController(
            A.Fake<IDistributedCacheService>(),
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            _systemContext);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsNotFound_WhenModelNotInCatalog()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "NonExistent-1.0.0"
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub",
                A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns((CkCompiledModelRoot?)null);

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsBadRequest_WhenCatalogNameEmpty()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto { CatalogName = "", ModelId = "Energy-1.0.0" };

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsBadRequest_WhenModelIdEmpty()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto { CatalogName = "PublicGitHub", ModelId = "" };

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsNone_WhenModelAlreadyInstalled()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "System-1.0.0"
        };
        var compiledModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("System", "1.0.0"),
            Dependencies = null
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub",
                A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(compiledModel);
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(
                A<CkModelId>.That.Matches(m => m.FullName == "System-1.0.0")))
            .Returns(true);

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DependencyResolutionResponseDto>().Subject;
        response.RootModel.Action.Should().Be("none");
        response.RootModel.InstalledVersion.Should().Be("1.0.0");
        response.RootModel.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsInstall_WhenModelNotInstalled()
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
            Dependencies = null
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub",
                A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(compiledModel);
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>.Ignored))
            .Returns(false);

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DependencyResolutionResponseDto>().Subject;
        response.RootModel.Action.Should().Be("install");
        response.RootModel.InstalledVersion.Should().BeNull();
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsTreeWithDependencies()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "Energy-2.0.0"
        };
        var systemDep = new CkModelId("System", "1.0.0");
        var compiledModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("Energy", "2.0.0"),
            Dependencies = [systemDep]
        };
        var systemModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("System", "1.0.0"),
            Dependencies = null
        };

        // Energy not in catalog-specific call, but in any-catalog call for sub-dep
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub",
                A<CkModelId>.That.Matches(m => m.Name == "Energy"),
                A<OperationResult>.Ignored, A<CancellationToken?>.Ignored))
            .Returns(compiledModel);
        A.CallTo(() => _catalogService.GetAsync(
                A<CkModelId>.That.Matches(m => m.Name == "System"),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(systemModel);

        // Energy not installed, System is installed
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(
                A<CkModelId>.That.Matches(m => m.Name == "Energy")))
            .Returns(false);
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(
                A<CkModelId>.That.Matches(m => m.Name == "System")))
            .Returns(true);

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<DependencyResolutionResponseDto>().Subject;

        response.RootModel.Name.Should().Be("Energy");
        response.RootModel.Action.Should().Be("install");
        response.RootModel.Dependencies.Should().HaveCount(1);

        var systemItem = response.RootModel.Dependencies[0];
        systemItem.Name.Should().Be("System");
        systemItem.Action.Should().Be("none");
        systemItem.InstalledVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task ResolveDependencies_ReturnsInternalServerError_OnException()
    {
        // Arrange
        var request = new ImportFromCatalogRequestDto
        {
            CatalogName = "PublicGitHub",
            ModelId = "Energy-1.0.0"
        };
        A.CallTo(() => _catalogService.GetAsync(A<string>.Ignored,
                A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Throws(new Exception("Service unavailable"));

        // Act
        var result = await _controller.ResolveDependencies(request, TestContext.Current.CancellationToken);

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
