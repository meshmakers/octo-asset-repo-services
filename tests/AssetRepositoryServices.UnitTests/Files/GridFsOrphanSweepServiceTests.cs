using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.OrphanSweep;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Files;

/// <summary>
///     AB#6541: the service resolves the tenant database, refuses a grace period below the engine minimum, maps the
///     engine result to the count report and runs one sweep per tenant at a time.
/// </summary>
public class GridFsOrphanSweepServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IGridFsOrphanSweepExecutor _executor = A.Fake<IGridFsOrphanSweepExecutor>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GridFsOrphanSweepService CreateService(string? databaseName = "customerdb")
    {
        if (databaseName == null)
        {
            A.CallTo(() => _systemContext.TryFindTenantContextAsync("customer")).Returns((ITenantContext?)null);
        }
        else
        {
            var tenant = A.Fake<ITenantContext>();
            A.CallTo(() => tenant.DatabaseName).Returns(databaseName);
            A.CallTo(() => _systemContext.TryFindTenantContextAsync("customer")).Returns(tenant);
        }

        return new GridFsOrphanSweepService(_systemContext, _executor);
    }

    private static GridFsOrphanSweepResult Result(bool removed) => new()
    {
        Removed = removed,
        GracePeriod = TimeSpan.FromHours(24),
        ExaminedFiles = 10,
        ReferencedFiles = 7,
        YoungFiles = 5,
        OrphanFiles = 3,
        OrphanChunks = 9,
        RemovedFiles = removed ? 3 : 0,
        OrphanBytes = 4096,
        Duration = TimeSpan.FromMilliseconds(250)
    };

    [Fact]
    public async Task GracePeriodBelowTheMinimum_IsRefused_BeforeAnythingIsLookedUp()
    {
        var service = CreateService();

        var act = () => service.SweepAsync("customer", true, TimeSpan.FromMinutes(9), Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        A.CallTo(_executor).MustNotHaveHappened();
        A.CallTo(_systemContext).MustNotHaveHappened();
    }

    [Fact]
    public async Task UnknownTenant_ReturnsNull_AndSweepsNothing()
    {
        var service = CreateService(databaseName: null);

        (await service.SweepAsync("customer", false, TimeSpan.FromHours(24), Ct)).Should().BeNull();
        A.CallTo(_executor).MustNotHaveHappened();
    }

    [Fact]
    public async Task DryRun_SweepsTheTenantDatabase_AndMapsTheCounts()
    {
        var service = CreateService();
        A.CallTo(() => _executor.SweepAsync("customerdb", A<GridFsOrphanSweepOptions>._, A<CancellationToken>._))
            .Returns(Result(removed: false));

        var report = await service.SweepAsync("customer", false, TimeSpan.FromHours(24), Ct);

        A.CallTo(() => _executor.SweepAsync("customerdb",
                A<GridFsOrphanSweepOptions>.That.Matches(o => !o.Remove && o.GracePeriod == TimeSpan.FromHours(24)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        report.Should().NotBeNull();
        report!.TenantId.Should().Be("customer");
        report.Mode.Should().Be("dry-run");
        report.GracePeriodMinutes.Should().Be(1440);
        report.ExaminedFiles.Should().Be(10);
        report.ReferencedFiles.Should().Be(7);
        report.YoungFiles.Should().Be(5);
        report.OrphanFiles.Should().Be(3);
        report.OrphanChunks.Should().Be(9);
        report.RemovedFiles.Should().Be(0);
        report.OrphanBytes.Should().Be(4096);
        report.DurationMilliseconds.Should().Be(250);
    }

    [Fact]
    public async Task Remove_IsPassedToTheEngine_AndReported()
    {
        var service = CreateService();
        A.CallTo(() => _executor.SweepAsync("customerdb", A<GridFsOrphanSweepOptions>._, A<CancellationToken>._))
            .Returns(Result(removed: true));

        var report = await service.SweepAsync("customer", true, TimeSpan.FromMinutes(10), Ct);

        A.CallTo(() => _executor.SweepAsync("customerdb",
                A<GridFsOrphanSweepOptions>.That.Matches(o => o.Remove && o.GracePeriod == TimeSpan.FromMinutes(10)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        report!.Mode.Should().Be("remove");
        report.RemovedFiles.Should().Be(3);
    }

    [Fact]
    public async Task SecondSweepOfTheSameTenant_WhileOneRuns_IsRejected_AndTheGateIsReleasedAfterwards()
    {
        var service = CreateService();
        var release = new TaskCompletionSource<GridFsOrphanSweepResult>();
        A.CallTo(() => _executor.SweepAsync("customerdb", A<GridFsOrphanSweepOptions>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(release.Task, Task.FromResult(Result(false)));

        var first = service.SweepAsync("customer", false, TimeSpan.FromHours(24), Ct);
        var second = () => service.SweepAsync("customer", false, TimeSpan.FromHours(24), Ct);
        await second.Should().ThrowAsync<GridFsOrphanSweepBusyException>();

        release.SetResult(Result(false));
        (await first).Should().NotBeNull();
        (await service.SweepAsync("customer", false, TimeSpan.FromHours(24), Ct)).Should().NotBeNull();
    }
}
