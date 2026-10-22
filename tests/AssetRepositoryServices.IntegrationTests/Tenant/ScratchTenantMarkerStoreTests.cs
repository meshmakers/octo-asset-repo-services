using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#7192 - the scratch marker store against the real engine and MongoDB. The case that broke the first local MCP
///     test: a create / attach / delete without the scratch flag asks to delete a marker that does not exist, and the
///     engine throws "Entity filter returns not exactly one entity" for such a delete.
/// </summary>
[Collection(AssetRepoCollection.Name)]
public class ScratchTenantMarkerStoreTests(AssetRepoFixture fixture)
{
    private IScratchTenantMarkerStore NewStore()
    {
        var octoService = A.Fake<IOctoService>();
        A.CallTo(() => octoService.SystemContext).Returns(fixture.GetSystemContext());
        return new ScratchTenantMarkerStore(octoService);
    }

    [Fact]
    public async Task Delete_OfAMarkerThatDoesNotExist_IsANoOp()
    {
        var store = NewStore();

        var act = () => store.DeleteAsync($"nomarker-{Guid.NewGuid():N}");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetGetDelete_RoundTripsTheMarker()
    {
        var store = NewStore();
        var tenantId = $"roundtrip-{Guid.NewGuid():N}";
        var marker = new ScratchTenantMarker
        {
            ParentTenantId = "octosystem", CreatorClientId = "agent", CreatedAtUtc = DateTime.UtcNow
        };

        await store.SetAsync(tenantId, marker);
        var read = await store.GetAsync(tenantId);
        await store.DeleteAsync(tenantId);
        await store.DeleteAsync(tenantId);

        read.Should().NotBeNull();
        read!.CreatorClientId.Should().Be("agent");
        read.ParentTenantId.Should().Be("octosystem");
        (await store.GetAsync(tenantId)).Should().BeNull();
    }

    [Fact]
    public async Task Set_Twice_ReplacesTheMarker()
    {
        var store = NewStore();
        var tenantId = $"replace-{Guid.NewGuid():N}";
        await store.SetAsync(tenantId,
            new ScratchTenantMarker { ParentTenantId = "p", CreatorClientId = "first", CreatedAtUtc = DateTime.UtcNow });
        await store.SetAsync(tenantId,
            new ScratchTenantMarker { ParentTenantId = "p", CreatorClientId = "second", CreatedAtUtc = DateTime.UtcNow });

        (await store.GetAsync(tenantId))!.CreatorClientId.Should().Be("second");
        await store.DeleteAsync(tenantId);
    }
}
