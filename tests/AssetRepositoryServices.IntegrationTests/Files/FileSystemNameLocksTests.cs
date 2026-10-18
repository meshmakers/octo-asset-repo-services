using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6477: lifetime rules of <see cref="FileSystemNameLocks" /> (owned by a session, released as a whole).
/// </summary>
[Collection(FilesCollection.Name)]
public class FileSystemNameLocksTests(FilesTestFixture fixture, ITestOutputHelper output)
{
    private ITenantRepository Repository
    {
        get
        {
            fixture.OutputHelper = output;
            return fixture.GetSystemContext().GetTenantRepository();
        }
    }

    [Fact]
    public async Task SecondSession_WaitsForTheFirstRelease_AndTheSameSessionIsReentrant()
    {
        var locks = new FileSystemNameLocks();
        using var first = Repository.GetSession();
        using var second = Repository.GetSession();

        await locks.AcquireAsync(first, ["k"]);
        await locks.AcquireAsync(first, ["k"]); // reentrant for the owner

        var waiting = locks.AcquireAsync(second, ["k"]);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();

        await locks.ReleaseAsync(first);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await locks.ReleaseAsync(second);
        await locks.ReleaseAsync(second); // idempotent
        locks.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task Timeout_Answers409_AndLeavesNothingBehind()
    {
        var locks = new FileSystemNameLocks();
        using var first = Repository.GetSession();
        using var second = Repository.GetSession();
        await locks.AcquireAsync(first, ["b"]);

        var act = () => locks.AcquireAsync(second, ["a", "b"], TimeSpan.FromMilliseconds(100));
        (await act.Should().ThrowAsync<FileSystemException>()).Which.StatusCode.Should().Be(409);

        await locks.ReleaseAsync(first);
        locks.ActiveCount.Should().Be(0);
        await locks.AcquireAsync(second, ["a", "b"], TimeSpan.FromMilliseconds(100)); // "a" was given back
        await locks.ReleaseAsync(second);
    }
}
