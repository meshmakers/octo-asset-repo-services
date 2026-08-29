using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// Integration tests for Tenant API Blueprint operations.
/// Tests ITenantBlueprintHistory and IBlueprintService.
/// </summary>
[Collection(BlueprintCollection.Name)]
public class BlueprintTests(BlueprintTestFixture fixture)
{
    [Fact]
    public async Task GetHistoryAsync_ShouldReturnEmptyList_ForNewTenant()
    {
        var blueprintHistory = fixture.GetBlueprintHistory();
        var tenantId = fixture.TestTenantId;

        var history = await blueprintHistory.GetHistoryAsync(tenantId, CancellationToken.None);

        Assert.NotNull(history);
        Assert.Empty(history);
    }

    [Fact]
    public async Task GetCurrentAsync_ShouldReturnNull_ForTenantWithoutBlueprint()
    {
        var blueprintHistory = fixture.GetBlueprintHistory();
        var tenantId = fixture.TestTenantId;

        var current = await blueprintHistory.GetCurrentAsync(tenantId, CancellationToken.None);

        Assert.Null(current);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_ShouldReturnNull_ForTenantWithoutBlueprint()
    {
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = fixture.TestTenantId;

        var updateInfo = await blueprintService.GetUpdateInfoAsync(tenantId, CancellationToken.None);

        Assert.Null(updateInfo);
    }

    /// <summary>
    /// AB#4832: the name-filtered lookups the tenant API exposes as
    /// <c>?blueprintName=</c> / <c>blueprintName:</c>.
    /// </summary>
    [Fact]
    public async Task GetCurrentByBlueprintNameAsync_ShouldReturnNull_ForTenantWithoutBlueprint()
    {
        var blueprintHistory = fixture.GetBlueprintHistory();
        var tenantId = fixture.TestTenantId;

        var current = await blueprintHistory.GetCurrentByBlueprintNameAsync(
            tenantId, "NoSuchBlueprint", CancellationToken.None);

        Assert.Null(current);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_WithBlueprintName_ShouldReturnNull_ForTenantWithoutBlueprint()
    {
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = fixture.TestTenantId;

        var updateInfo = await blueprintService.GetUpdateInfoAsync(
            tenantId, "NoSuchBlueprint", CancellationToken.None);

        Assert.Null(updateInfo);
    }
}
