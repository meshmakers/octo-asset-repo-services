using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
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
    private readonly ICkModelLibraryStatusService _libraryStatusService;
    private readonly ModelsController _controller;

    public ModelsControllerResolveDependenciesTests()
    {
        _catalogService = A.Fake<ICatalogService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();

        A.CallTo(() => _systemContext.FindTenantContextAsync("test-tenant"))
            .Returns(_tenantContext);

        // AB#5432: the compatibility pre-checks moved out of the controller into
        // ICkModelLibraryStatusService. Default the fake to "compatible, nothing installed" so
        // these tests keep exercising the behaviour they were written for; a bare fake would
        // return default((bool, string?)) == (false, null) and turn every import into a 400.
        _libraryStatusService = A.Fake<ICkModelLibraryStatusService>();
        A.CallTo(() => _libraryStatusService.GetInstalledSystemVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion>());
        A.CallTo(() => _libraryStatusService.CheckSystemCompatibilityAsync(
                A<CkModelId>._, A<Dictionary<string, CkVersion>>._, A<HashSet<string>>._,
                A<List<string>>._, A<CancellationToken>._))
            .Returns((true, (string?)null));

        _controller = new ModelsController(
            A.Fake<IDistributedCacheService>(),
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            _systemContext,
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelUpgradeService>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelMigrationService>(),
            _libraryStatusService);

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

    // G3 review A-M1: a dependency is judged by what the PARENT requires - an exact pin of a classic parent stays
    // exact (a newer installed version would make the parent ResolveFailed), a range-retaining parent accepts its
    // range from the floor; never across majors; a newer installed version is never offered for a downgrade.
    [Theory]
    [InlineData("Basic", "2.0.0", "2.0.0", "none")]
    [InlineData("Basic", "2.0.0", "2.1.0", "incompatible")]
    [InlineData("Basic", "2.0.0", "1.5.0", "install")]
    [InlineData("Basic", "2.0.0", null, "install")]
    [InlineData("System", "2.5.0", "2.6.1", "incompatible")]
    [InlineData("System", "2.5.0", "2.5.0", "none")]
    [InlineData("System", "2.5.0", null, "none")]
    public async Task ClassicParent_JudgesTheDependencyByItsExactPin(string name, string pin, string? installed,
        string expectedAction)
    {
        var dependency = await ResolveSingleDependencyAsync(new CkModelId(name, pin), null, installed);

        dependency.Action.Should().Be(expectedAction);
    }

    [Theory]
    [InlineData("Basic", "[2.0,3.0)", "2.0.0", "2.1.0", "none")]
    [InlineData("Basic", "[2.2,3.0)", "2.2.0", "2.1.0", "install")]
    [InlineData("Basic", "[2.0,3.0)", "2.0.0", "3.0.0", "incompatible")]
    [InlineData("System", "[2.5,3.0)", "2.5.0", "2.6.1", "none")]
    [InlineData("System", "[2.5,3.0)", "2.5.0", "3.0.0", "incompatible")]
    public async Task RangeRetainingParent_JudgesTheDependencyByItsRangeAndFloor(string name, string range,
        string floor, string installed, string expectedAction)
    {
        var dependencyRange = new CkModelDependencyDto
        {
            Range = new CkModelIdVersionRange($"{name}-{range}"),
            Floor = floor
        };

        var dependency = await ResolveSingleDependencyAsync(new CkModelId(name, floor), dependencyRange, installed);

        dependency.Action.Should().Be(expectedAction);
        dependency.RequiredVersion.Should().Be(floor, "RequiredVersion stays the pinned (floor) version");
        dependency.RequiredRange.Should().Be(dependencyRange.ToString());
    }

    [Fact]
    public async Task Root_WithANewerInstalledVersion_IsNotOfferedForADowngrade()
    {
        StubCatalogModel("Energy-2.0.0");
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>.Ignored)).Returns(false);
        A.CallTo(() => _libraryStatusService.GetInstalledModelVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion> { ["Energy"] = new("2.1.0") });

        var root = await ResolveRootAsync("Energy-2.0.0");

        root.Action.Should().Be("incompatible");
        root.InstalledVersion.Should().Contain("installed v2.1.0");
    }

    [Fact]
    public async Task Batch_WithARangeRetainingParent_DoesNotFail()
    {
        // Re-review N1: the batch tree correction parsed the range display string as a version and answered 500.
        var dependency = new CkModelId("Basic", "2.0.0");
        var parent = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("Parent", "1.0.0"),
            Dependencies = [dependency],
            DependencyRanges = [new CkModelDependencyDto { Range = new CkModelIdVersionRange("Basic-[2.0,3.0)"), Floor = "2.0.0" }]
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub", A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(parent);
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.Name == "Basic"),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot { ModelId = dependency, Dependencies = null });
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>.Ignored)).Returns(false);
        A.CallTo(() => _libraryStatusService.GetInstalledModelVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion>());

        var result = await _controller.ResolveDependenciesBatch(
            [new ImportFromCatalogRequestDto { CatalogName = "PublicGitHub", ModelId = "Parent-1.0.0" }],
            TestContext.Current.CancellationToken);

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<BatchDependencyResolutionResponseDto>().Subject;
        response.ModelsToImport.Should().Contain(["Basic-2.0.0", "Parent-1.0.0"]);
        var basic = response.DependencyTrees.Single().RootModel.Dependencies.Single();
        basic.Action.Should().Be("install");
        basic.RequiredVersion.Should().Be("2.0.0");
        basic.RequiredRange.Should().StartWith("Basic-[2.0");
    }

    [Fact]
    public async Task SharedDependency_IsJudgedAgainstEveryRequirement()
    {
        // Re-review N8: Lib-1.2.0 is required by a range parent (satisfied by the installed 1.3.0) and by a classic
        // parent (exact pin, not satisfied). The classic requirement must still be judged -> incompatible.
        var lib = new CkModelId("Lib", "1.2.0");
        var rangeParent = new CkModelId("RangeParent", "1.0.0");
        var classicParent = new CkModelId("ClassicParent", "1.0.0");
        var root = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("Root", "1.0.0"),
            Dependencies = [rangeParent, classicParent]
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub", A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(root);
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.Name == "RangeParent"),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot
            {
                ModelId = rangeParent, Dependencies = [lib],
                DependencyRanges = [new CkModelDependencyDto { Range = new CkModelIdVersionRange("Lib-[1.2,2.0)"), Floor = "1.2.0" }]
            });
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.Name == "ClassicParent"),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot { ModelId = classicParent, Dependencies = [lib] });
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.Name == "Lib"),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot { ModelId = lib, Dependencies = null });
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>.Ignored)).Returns(false);
        A.CallTo(() => _libraryStatusService.GetInstalledModelVersionsAsync(A<ITenantContext>._))
            .Returns(new Dictionary<string, CkVersion> { ["Lib"] = new("1.3.0") });

        var tree = await ResolveRootAsync("Root-1.0.0");

        var byParent = tree.Dependencies.ToDictionary(d => d.Name, d => d.Dependencies.Single().Action);
        byParent["RangeParent"].Should().Be("none");
        byParent["ClassicParent"].Should().Be("incompatible");
    }

    private async Task<DependencyResolutionItemDto> ResolveSingleDependencyAsync(CkModelId dependency,
        CkModelDependencyDto? range, string? installed)
    {
        var parent = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("Parent", "1.0.0"),
            Dependencies = [dependency],
            DependencyRanges = range == null ? null : [range]
        };
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub", A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(parent);
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.Name == dependency.Name),
                A<OperationResult>.Ignored, null, A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot { ModelId = dependency, Dependencies = null });
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>.Ignored))
            .ReturnsLazily((CkModelId id) => installed != null && id.Name == dependency.Name &&
                                             id.Version.CompareTo(new CkVersion(installed)) == 0);
        A.CallTo(() => _libraryStatusService.GetInstalledModelVersionsAsync(A<ITenantContext>._))
            .Returns(installed == null
                ? new Dictionary<string, CkVersion>()
                : new Dictionary<string, CkVersion> { [dependency.Name] = new(installed) });

        var root = await ResolveRootAsync("Parent-1.0.0");
        return root.Dependencies.Should().ContainSingle().Subject;
    }

    private void StubCatalogModel(string modelId)
    {
        A.CallTo(() => _catalogService.GetAsync("PublicGitHub",
                A<CkModelId>.Ignored, A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(new CkCompiledModelRoot { ModelId = new CkModelId(modelId), Dependencies = null });
    }

    private async Task<DependencyResolutionItemDto> ResolveRootAsync(string modelId)
    {
        var result = await _controller.ResolveDependencies(
            new ImportFromCatalogRequestDto { CatalogName = "PublicGitHub", ModelId = modelId },
            TestContext.Current.CancellationToken);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DependencyResolutionResponseDto>().Subject.RootModel;
    }
}
