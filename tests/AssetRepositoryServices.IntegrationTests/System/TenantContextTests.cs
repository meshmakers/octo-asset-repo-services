using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.System;

[Collection("Sequential")]
public class TenantContextTests(AssetRepoFixture fixture)
    : IClassFixture<AssetRepoFixture>
{
    [Fact]
    public async Task IsSystemTenantExisting()
    {
        var systemContext = fixture.GetSystemContext();
        var result = await systemContext.IsSystemTenantExistingAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task GetTestTenant_ShouldReturnTestTenantContext()
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var tenant = await systemContext.GetChildTenantAsync(session, fixture.TestTenantId);

        await session.CommitTransactionAsync();

        Assert.Equal(fixture.TestTenantId.ToLower(), tenant.TenantId);
        Assert.Equal(fixture.TestTenantId.ToLower(), tenant.DatabaseName);
    }

    [Fact]
    public async Task CreateAndDeleteChildTenant_ShouldSucceed()
    {
        var systemContext = fixture.GetSystemContext();
        var tempTenantId = $"temp-tenant-{Guid.NewGuid():N}";

        // Create tenant
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tempTenantId, tempTenantId);
            await session.CommitTransactionAsync();
        }

        // Verify tenant exists
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            var exists = await systemContext.IsChildTenantExistingAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
            Assert.True(exists);
        }

        // Delete tenant
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
        }

        // Verify tenant is deleted
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            var exists = await systemContext.IsChildTenantExistingAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
            Assert.False(exists);
        }
    }
}
