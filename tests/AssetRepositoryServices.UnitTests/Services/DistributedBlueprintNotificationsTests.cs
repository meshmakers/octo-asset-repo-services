using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Services;

/// <summary>
///     The asset repository forwards the CK model dependencies of the engine's applied and updated notifications
///     to the distribution event hub unchanged; an engine notification without them is published without them (the
///     consumers then enable nothing).
/// </summary>
public class DistributedBlueprintNotificationsTests
{
    private readonly IDistributionEventHubService _eventHub = A.Fake<IDistributionEventHubService>();
    private readonly List<object> _published = [];

    public DistributedBlueprintNotificationsTests()
    {
        A.CallTo(() => _eventHub.PublishAsync(A<BlueprintApplied>._, A<CancellationToken?>._))
            .Invokes((BlueprintApplied m, CancellationToken? _) => _published.Add(m))
            .Returns(Task.CompletedTask);
        A.CallTo(() => _eventHub.PublishAsync(A<BlueprintUpdated>._, A<CancellationToken?>._))
            .Invokes((BlueprintUpdated m, CancellationToken? _) => _published.Add(m))
            .Returns(Task.CompletedTask);
    }

    private DistributedBlueprintNotifications Create() =>
        new(_eventHub, NullLogger<DistributedBlueprintNotifications>.Instance);

    [Fact]
    public async Task Applied_ForwardsTheCkModelDependencies()
    {
        var notification = new BlueprintAppliedNotification("tenant-a", new BlueprintId("Starter-1.0.0"),
            BlueprintApplicationMode.Initial, 5, 0, 0, Guid.NewGuid(), DateTime.UtcNow)
        {
            CkModelDependencies = ["System.Ai", "System.Communication"]
        };

        await Create().NotifyAppliedAsync(notification, TestContext.Current.CancellationToken);

        var applied = _published.Should().ContainSingle().Which.Should().BeOfType<BlueprintApplied>().Subject;
        applied.TenantId.Should().Be("tenant-a");
        applied.BlueprintId.Should().Be("Starter-1.0.0");
        applied.CkModelDependencies.Should().Equal("System.Ai", "System.Communication");
    }

    [Fact]
    public async Task Updated_ForwardsTheCkModelDependencies()
    {
        var notification = new BlueprintUpdatedNotification("tenant-a", new BlueprintId("Starter-2.0.0"),
            new BlueprintId("Starter-1.0.0"), BlueprintUpdateMode.Merge, 0, 1, 0, Guid.NewGuid(), DateTime.UtcNow)
        {
            CkModelDependencies = ["System.Communication"]
        };

        await Create().NotifyUpdatedAsync(notification, TestContext.Current.CancellationToken);

        var updated = _published.Should().ContainSingle().Which.Should().BeOfType<BlueprintUpdated>().Subject;
        updated.FromVersion.Should().Be("Starter-1.0.0");
        updated.CkModelDependencies.Should().Equal("System.Communication");
    }

    [Fact]
    public async Task NotificationWithoutDependencies_IsPublishedWithoutThem()
    {
        var notification = new BlueprintAppliedNotification("tenant-a", new BlueprintId("Starter-1.0.0"),
            BlueprintApplicationMode.Initial, 1, 0, 0, Guid.NewGuid(), DateTime.UtcNow);

        await Create().NotifyAppliedAsync(notification, TestContext.Current.CancellationToken);

        _published.Should().ContainSingle().Which.Should().BeOfType<BlueprintApplied>()
            .Which.CkModelDependencies.Should().BeNull();
    }
}
