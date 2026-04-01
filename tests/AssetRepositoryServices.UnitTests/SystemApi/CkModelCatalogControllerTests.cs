using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

public class CkModelCatalogControllerTests
{
    private readonly ICatalogService _catalogService;
    private readonly CkModelCatalogController _controller;

    public CkModelCatalogControllerTests()
    {
        _catalogService = A.Fake<ICatalogService>();
        _controller = new CkModelCatalogController(_catalogService);
    }

    #region GetAll

    [Fact]
    public async Task GetAll_ReturnsOkWithPaginatedList()
    {
        // Arrange
        var modelResult = CreateModelListResult(2);
        A.CallTo(() => _catalogService.ListAsync(0, 20, null, A<CancellationToken?>.Ignored))
            .Returns(modelResult);

        // Act
        var result = await _controller.GetAll(null, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CkModelCatalogListResponseDto>().Subject;
        response.Items.Should().HaveCount(2);
        response.TotalCount.Should().Be(2);
        response.Skip.Should().Be(0);
        response.Take.Should().Be(20);
    }

    [Fact]
    public async Task GetAll_UsesPagingParameters()
    {
        // Arrange
        var pagingParams = new Meshmakers.Octo.Communication.Contracts.DataTransferObjects.PagingParams
        {
            Skip = 5,
            Take = 10
        };
        var modelResult = CreateModelListResult(0, 5, 10);
        A.CallTo(() => _catalogService.ListAsync(5, 10, null, A<CancellationToken?>.Ignored))
            .Returns(modelResult);

        // Act
        var result = await _controller.GetAll(pagingParams, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CkModelCatalogListResponseDto>().Subject;
        response.Skip.Should().Be(5);
        response.Take.Should().Be(10);
    }

    [Fact]
    public async Task GetAll_ReturnsInternalServerError_OnException()
    {
        // Arrange
        A.CallTo(() => _catalogService.ListAsync(A<int>.Ignored, A<int>.Ignored, null, A<CancellationToken?>.Ignored))
            .Throws(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.GetAll(null, TestContext.Current.CancellationToken);

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        statusResult.Value.Should().BeOfType<InternalServerErrorDto>();
    }

    #endregion

    #region Search

    [Fact]
    public async Task Search_ReturnsOkWithResults()
    {
        // Arrange
        var searchResult = new ModelSearchResult
        {
            TotalCount = 1,
            SkippedCount = 0,
            TakeCount = 20,
            ModelResultItems = new List<CatalogResultItem>
            {
                CreateCatalogResultItem("Energy", "1.0.0", "LocalFileSystem")
            },
            SearchTerm = "Energy"
        };
        A.CallTo(() => _catalogService.SearchAsync("Energy", 0, 20, null, A<CancellationToken?>.Ignored))
            .Returns(searchResult);

        // Act
        var result = await _controller.Search("Energy", null, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CkModelCatalogListResponseDto>().Subject;
        response.Items.Should().HaveCount(1);
        response.Items[0].Name.Should().Be("Energy");
    }

    [Fact]
    public async Task Search_ReturnsBadRequest_WhenSearchTermEmpty()
    {
        // Act
        var result = await _controller.Search("", null, TestContext.Current.CancellationToken);

        // Assert
        var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequestResult.Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task Search_ReturnsBadRequest_WhenSearchTermWhitespace()
    {
        // Act
        var result = await _controller.Search("   ", null, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GetCatalogs

    [Fact]
    public void GetCatalogs_ReturnsOkWithCatalogList()
    {
        // Arrange
        var catalogs = new List<Tuple<string, string>>
        {
            Tuple.Create("LocalFileSystem", "Local file system catalog"),
            Tuple.Create("PublicGitHub", "Public GitHub catalog")
        };
        A.CallTo(() => _catalogService.GetCatalogList(null)).Returns(catalogs);

        // Act
        var result = _controller.GetCatalogs();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IEnumerable<CkModelCatalogDto>>().Subject.ToList();
        response.Should().HaveCount(2);
        response[0].Name.Should().Be("LocalFileSystem");
        response[0].Description.Should().Be("Local file system catalog");
        response[1].Name.Should().Be("PublicGitHub");
    }

    [Fact]
    public void GetCatalogs_ReturnsInternalServerError_OnException()
    {
        // Arrange
        A.CallTo(() => _catalogService.GetCatalogList(null))
            .Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.GetCatalogs();

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    #endregion

    #region GetByCatalog

    [Fact]
    public async Task GetByCatalog_ReturnsOkWithFilteredList()
    {
        // Arrange
        var modelResult = CreateModelListResult(1);
        A.CallTo(() =>
                _catalogService.ListAsync("LocalFileSystem", 0, 20, null, A<CancellationToken?>.Ignored))
            .Returns(modelResult);

        // Act
        var result = await _controller.GetByCatalog("LocalFileSystem", null, TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CkModelCatalogListResponseDto>().Subject;
        response.Items.Should().HaveCount(1);
    }

    #endregion

    #region Exists

    [Fact]
    public async Task Exists_ReturnsOk_WhenModelExists()
    {
        // Arrange
        A.CallTo(() => _catalogService.IsExistingAsync(
                A<CkModelId>.That.Matches(m => m.FullName == "System-1.0.0"), null))
            .Returns(true);

        // Act
        var result = await _controller.Exists("System-1.0.0");

        // Assert
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Exists_ReturnsNotFound_WhenModelDoesNotExist()
    {
        // Arrange
        A.CallTo(() => _catalogService.IsExistingAsync(A<CkModelId>.Ignored, null))
            .Returns(false);

        // Act
        var result = await _controller.Exists("NonExistent-1.0.0");

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    #endregion

    #region GetModel

    [Fact]
    public async Task GetModel_ReturnsOk_WhenModelFound()
    {
        // Arrange
        var compiledModel = new CkCompiledModelRoot
        {
            ModelId = new CkModelId("System", "1.0.0"),
            Description = "System model"
        };
        A.CallTo(() => _catalogService.GetAsync("LocalFileSystem",
                A<CkModelId>.That.Matches(m => m.FullName == "System-1.0.0"),
                A<OperationResult>.Ignored,
                A<CancellationToken?>.Ignored))
            .Returns(compiledModel);

        // Act
        var result = await _controller.GetModel("LocalFileSystem", "System-1.0.0", TestContext.Current.CancellationToken);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CkModelCatalogItemDto>().Subject;
        response.Id.Should().Be("System-1.0.0");
        response.Name.Should().Be("System");
        response.Version.Should().Be("1.0.0");
        response.Description.Should().Be("System model");
        response.CatalogName.Should().Be("LocalFileSystem");
    }

    [Fact]
    public async Task GetModel_ReturnsNotFound_WhenModelNotFound()
    {
        // Arrange
        A.CallTo(() => _catalogService.GetAsync(A<string>.Ignored, A<CkModelId>.Ignored,
                A<OperationResult>.Ignored, A<CancellationToken?>.Ignored))
            .Returns((CkCompiledModelRoot?)null);

        // Act
        var result = await _controller.GetModel("LocalFileSystem", "NonExistent-1.0.0", TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    #endregion

    #region Refresh

    [Fact]
    public async Task RefreshAll_ReturnsNoContent()
    {
        // Arrange
        A.CallTo(() => _catalogService.RefreshAllCatalogCachesAsync(null))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RefreshAll();

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RefreshCatalog_ReturnsNoContent()
    {
        // Arrange
        A.CallTo(() => _catalogService.RefreshCatalogCacheAsync("LocalFileSystem", null))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RefreshCatalog("LocalFileSystem");

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RefreshAll_ReturnsInternalServerError_OnException()
    {
        // Arrange
        A.CallTo(() => _catalogService.RefreshAllCatalogCachesAsync(null))
            .Throws(new InvalidOperationException("Refresh failed"));

        // Act
        var result = await _controller.RefreshAll();

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    #endregion

    #region Helpers

    private static ModelListResult CreateModelListResult(int count, int skip = 0, int take = 20)
    {
        var items = new List<CatalogResultItem>();
        for (var i = 0; i < count; i++)
        {
            items.Add(CreateCatalogResultItem($"Model{i}", "1.0.0", "LocalFileSystem"));
        }

        return new ModelListResult
        {
            TotalCount = count,
            SkippedCount = skip,
            TakeCount = take,
            ModelResultItems = items
        };
    }

    private static CatalogResultItem CreateCatalogResultItem(string name, string version, string catalogName)
    {
        return new CatalogResultItem
        {
            ModelId = new CkModelId(name, version),
            Description = $"{name} model",
            CatalogName = catalogName
        };
    }

    #endregion
}
