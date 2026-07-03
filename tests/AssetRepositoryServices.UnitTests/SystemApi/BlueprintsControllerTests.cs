using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

public class BlueprintsControllerTests
{
    private readonly IBlueprintCatalogManager _catalogManager;
    private readonly BlueprintsController _controller;

    public BlueprintsControllerTests()
    {
        _catalogManager = A.Fake<IBlueprintCatalogManager>();
        _controller = new BlueprintsController(_catalogManager);
    }

    #region RefreshAllCatalogs

    [Fact]
    public async Task RefreshAllCatalogs_ReturnsOkWithPerCatalogResults()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshAllCatalogCachesAsync(null, true))
            .Returns(new List<BlueprintCatalogRefreshResult>
            {
                CreateRefreshResult("EmbeddedResourceBlueprintCatalog", BlueprintCatalogRefreshStatus.Refreshed),
                CreateRefreshResult("PrivateGitHubBlueprintCatalog", BlueprintCatalogRefreshStatus.Failed, "boom")
            });

        // Act
        var result = await _controller.RefreshAllCatalogs();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BlueprintCatalogRefreshResponseDto>().Subject;
        response.Results.Should().HaveCount(2);
        response.Results[0].CatalogName.Should().Be("EmbeddedResourceBlueprintCatalog");
        response.Results[0].Status.Should().Be("Refreshed");
        response.Results[0].Message.Should().BeNull();
        response.Results[1].Status.Should().Be("Failed");
        response.Results[1].Message.Should().Be("boom");
    }

    [Fact]
    public async Task RefreshAllCatalogs_ForcesTheRefresh()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshAllCatalogCachesAsync(null, true))
            .Returns(new List<BlueprintCatalogRefreshResult>());

        // Act
        await _controller.RefreshAllCatalogs();

        // Assert
        A.CallTo(() => _catalogManager.RefreshAllCatalogCachesAsync(null, true))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RefreshAllCatalogs_ReturnsInternalServerError_OnException()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshAllCatalogCachesAsync(A<object?>.Ignored, A<bool>.Ignored))
            .Throws(new InvalidOperationException("Refresh failed"));

        // Act
        var result = await _controller.RefreshAllCatalogs();

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        statusResult.Value.Should().BeOfType<InternalServerErrorDto>();
    }

    #endregion

    #region RefreshCatalog

    [Fact]
    public async Task RefreshCatalog_ReturnsOkWithSingleResult()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshCatalogCacheAsync("LocalFileSystemBlueprintCatalog", null, true))
            .Returns(CreateRefreshResult("LocalFileSystemBlueprintCatalog", BlueprintCatalogRefreshStatus.Refreshed));

        // Act
        var result = await _controller.RefreshCatalog("LocalFileSystemBlueprintCatalog");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BlueprintCatalogRefreshResponseDto>().Subject;
        var item = response.Results.Should().ContainSingle().Subject;
        item.CatalogName.Should().Be("LocalFileSystemBlueprintCatalog");
        item.Status.Should().Be("Refreshed");
    }

    [Fact]
    public async Task RefreshCatalog_ReturnsNotFound_WhenCatalogUnknown()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshCatalogCacheAsync("Missing", null, true))
            .Throws(BlueprintCatalogException.CatalogNotFound("Missing"));

        // Act
        var result = await _controller.RefreshCatalog("Missing");

        // Assert
        var notFoundResult = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        var error = notFoundResult.Value.Should().BeOfType<NotFoundErrorDto>().Subject;
        error.Message.Should().Contain("Missing");
    }

    [Fact]
    public async Task RefreshCatalog_ReturnsInternalServerError_OnUnexpectedException()
    {
        // Arrange
        A.CallTo(() => _catalogManager.RefreshCatalogCacheAsync(A<string>.Ignored, A<object?>.Ignored, A<bool>.Ignored))
            .Throws(new InvalidOperationException("Unexpected"));

        // Act
        var result = await _controller.RefreshCatalog("LocalFileSystemBlueprintCatalog");

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        statusResult.Value.Should().BeOfType<InternalServerErrorDto>();
    }

    #endregion

    #region Helpers

    private static BlueprintCatalogRefreshResult CreateRefreshResult(string catalogName,
        BlueprintCatalogRefreshStatus status, string? message = null)
    {
        return new BlueprintCatalogRefreshResult
        {
            CatalogName = catalogName,
            Status = status,
            Message = message
        };
    }

    #endregion
}
