using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// Integration tests for Tenant API Blueprint operations.
/// Tests ITenantBlueprintHistory, ITenantBackupService, and IBlueprintService.
/// </summary>
[Collection("Sequential")]
public class BlueprintTests(BlueprintTestFixture fixture)
    : IClassFixture<BlueprintTestFixture>
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
    public async Task ListBackupsAsync_ShouldReturnEmptyList_ForNewTenant()
    {
        var backupService = fixture.GetBackupService();
        var tenantId = fixture.TestTenantId;

        var backups = await backupService.ListBackupsAsync(tenantId, CancellationToken.None);

        Assert.NotNull(backups);
        Assert.Empty(backups);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_ShouldReturnNull_ForTenantWithoutBlueprint()
    {
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = fixture.TestTenantId;

        var updateInfo = await blueprintService.GetUpdateInfoAsync(tenantId, CancellationToken.None);

        Assert.Null(updateInfo);
    }
}
