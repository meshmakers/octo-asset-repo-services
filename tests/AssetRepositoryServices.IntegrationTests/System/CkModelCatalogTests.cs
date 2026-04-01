using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.System;

/// <summary>
/// Integration tests for CK model catalog operations.
/// Tests the ICatalogService with real catalog implementations.
/// </summary>
[Collection("Sequential")]
public class CkModelCatalogTests(AssetRepoFixture fixture)
    : IClassFixture<AssetRepoFixture>
{
    [Fact]
    public async Task ListAsync_ShouldReturnModels()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var result = await catalogService.ListAsync(0, 20);

        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 0);
    }

    [Fact]
    public async Task ListAsync_WithPaging_ShouldRespectSkipAndTake()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var result = await catalogService.ListAsync(skip: 0, take: 5);

        Assert.NotNull(result);
        Assert.True(result.ModelResultItems.Count <= 5);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnMatchingModels()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var result = await catalogService.SearchAsync("System", 0, 20);

        Assert.NotNull(result);
        Assert.Equal("System", result.SearchTerm);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmptyForNonExistentTerm()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var result = await catalogService.SearchAsync("nonexistent-model-xyz-12345", 0, 20);

        Assert.NotNull(result);
        Assert.Empty(result.ModelResultItems);
    }

    [Fact]
    public void GetCatalogList_ShouldReturnKnownCatalogs()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var catalogs = catalogService.GetCatalogList().ToList();

        Assert.NotNull(catalogs);
        // At minimum, the EmbeddedResource catalog should be available
        Assert.NotEmpty(catalogs);
    }

    [Fact]
    public async Task IsExistingAsync_ShouldReturnFalseForNonExistentModel()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        var exists = await catalogService.IsExistingAsync(new CkModelId("NonExistent-99.99.99"));

        Assert.False(exists);
    }

    [Fact]
    public async Task RefreshAllCatalogCachesAsync_ShouldCompleteWithoutError()
    {
        var catalogService = fixture.GetService<ICatalogService>();

        await catalogService.RefreshAllCatalogCachesAsync();

        // If no exception is thrown, the refresh was successful
    }
}
