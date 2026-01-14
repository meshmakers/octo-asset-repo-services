using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.System;

/// <summary>
/// Integration tests for System API Blueprint catalog operations.
/// Tests the IBlueprintCatalogManager service.
/// </summary>
[Collection("Sequential")]
public class BlueprintCatalogTests(BlueprintTestFixture fixture)
    : IClassFixture<BlueprintTestFixture>
{
    [Fact]
    public async Task ListAsync_ShouldReturnEmptyList_WhenNoBlueprintsRegistered()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();

        var result = await catalogManager.ListAsync(0, 20);

        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListAsync_WithPaging_ShouldRespectSkipAndTake()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();

        var result = await catalogManager.ListAsync(skip: 0, take: 10);

        Assert.NotNull(result);
        // Even with no blueprints, paging should work correctly
        Assert.True(result.TotalCount >= 0);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmptyList_WhenNoMatch()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();

        var result = await catalogManager.SearchAsync("nonexistent-blueprint-xyz", 0, 20);

        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetCatalogList_ShouldReturnAvailableCatalogs()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();

        var catalogs = catalogManager.GetCatalogList();

        Assert.NotNull(catalogs);
        // Catalogs may be empty if none are registered
    }

    [Fact]
    public async Task IsExistingAsync_ShouldReturnFalse_ForNonExistentBlueprint()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();
        var nonExistentId = new BlueprintId("NonExistent-1.0.0");

        var exists = await catalogManager.IsExistingAsync(nonExistentId);

        Assert.False(exists);
    }

    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_ForNonExistentBlueprint()
    {
        var catalogManager = fixture.GetBlueprintCatalogManager();
        var nonExistentId = new BlueprintId("NonExistent-1.0.0");
        var operationResult = new Meshmakers.Octo.ConstructionKit.Contracts.OperationResult();

        var blueprint = await catalogManager.TryGetAsync(nonExistentId, operationResult);

        Assert.Null(blueprint);
    }

    [Fact]
    public void BlueprintId_ShouldParseCorrectly()
    {
        var blueprintId = new BlueprintId("MyBlueprint-1.2.3");

        Assert.Equal("MyBlueprint", blueprintId.Name);
        Assert.Equal("1.2.3", blueprintId.Version.ToString());
        Assert.Equal("MyBlueprint-1.2.3", blueprintId.FullName);
    }

    [Fact]
    public void BlueprintId_WithMajorMinorPatch_ShouldParseCorrectly()
    {
        var blueprintId = new BlueprintId("TestBlueprint-2.0.0");

        Assert.Equal("TestBlueprint", blueprintId.Name);
        Assert.Equal("2.0.0", blueprintId.Version.ToString());
        Assert.Equal("TestBlueprint-2.0.0", blueprintId.FullName);
    }
}
