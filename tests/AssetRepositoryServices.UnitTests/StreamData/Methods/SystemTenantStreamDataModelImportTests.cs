using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData.Methods;

/// <summary>
///     AB#8722: after the import of System.StreamData the system tenant's CK cache is loaded and holds the model, because the
///     startup validation of the in-process method registrations reads only a loaded cache. The paths the integration tests
///     (<c>SystemTenantStreamDataImportReplicaTests</c>) cannot force: a cache loaded concurrently from the state before the
///     import, and a model that is not installed after the import.
/// </summary>
public class SystemTenantStreamDataModelImportTests
{
    private const string TenantId = "octosystem";

    private static readonly CkModelId Embedded = SystemStreamDataCkIds.CkModelId;

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenant = A.Fake<ITenantContext>();
    private readonly ICkCacheService _ckCache = A.Fake<ICkCacheService>();

    public SystemTenantStreamDataModelImportTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns(TenantId);
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(_tenant);
        A.CallTo(() => _tenant.TenantId).Returns(TenantId);
        A.CallTo(() => _tenant.IsCkModelSatisfiedAsync(Embedded)).Returns(true);
        A.CallTo(() => _ckCache.IsTenantLoaded(TenantId)).Returns(true);
    }

    private SystemTenantStreamDataModelImport Import() =>
        new(_systemContext, _ckCache, NullLogger<SystemTenantStreamDataModelImport>.Instance);

    private static ICollection<CkModelId> Models(params string[] ids) => ids.Select(id => new CkModelId(id)).ToList();

    [Fact]
    public async Task TheCacheIsLoadedAfterTheImport()
    {
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).Returns(Models("System-2.0.0", Embedded.FullName));

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.ImportCkModelWithDowngradeGuardAsync(Embedded)).MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustHaveHappenedOnceExactly());
        A.CallTo(() => _ckCache.Unload(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ANewerInstalledVersion_SatisfiesTheCheck()
    {
        var newer = $"{Embedded.Name}-{Embedded.Version.Major}.{Embedded.Version.Minor + 1}.0";
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).Returns(Models("System-2.0.0", newer));

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _ckCache.Unload(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ACacheLoadedFromTheStateBeforeTheImport_IsLoadedAgain()
    {
        // first look: a cache another load in this process built before the import committed
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).ReturnsNextFromSequence(
            Models("System-2.0.0"), Models("System-2.0.0", Embedded.FullName));

        await Import().InitializeAsync();

        A.CallTo(() => _ckCache.Unload(TenantId)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ACacheThatNeverHoldsTheModel_IsLoadedABoundedNumberOfTimes()
    {
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).Returns(Models("System-2.0.0"));

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.LoadCacheForTenantAsync())
            .MustHaveHappened(SystemTenantStreamDataModelImport.MaxCacheLoadAttempts, Times.Exactly);
        A.CallTo(() => _ckCache.Unload(TenantId))
            .MustHaveHappened(SystemTenantStreamDataModelImport.MaxCacheLoadAttempts - 1, Times.Exactly);
    }

    [Fact]
    public async Task ALoadThatRanOutWaitingForAnotherImport_IsRetried()
    {
        var marked = new InvalidOperationException("load failed");
        marked.Data[CkModelImportInProgress.ExceptionDataKey] = "System.StreamData";
        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).Throws(marked).Once();
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).Returns(Models("System-2.0.0", Embedded.FullName));

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ALoadThatFailsForAnotherReason_FailsTheStep()
    {
        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).Throws(new InvalidOperationException("broken model"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Import().InitializeAsync());
    }

    [Fact]
    public async Task ACacheUnloadedWhileItIsChecked_IsLoadedAgain()
    {
        A.CallTo(() => _ckCache.GetCkModelIds(TenantId)).Throws(new CkCacheException("unloaded")).Once()
            .Then.Returns(Models("System-2.0.0", Embedded.FullName));

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task AModelThatIsNotInstalled_IsLeftToTheValidation()
    {
        // e.g. the import was skipped for missing dependencies: the validation must still fail with its own error
        A.CallTo(() => _tenant.IsCkModelSatisfiedAsync(Embedded)).Returns(false);

        await Import().InitializeAsync();

        A.CallTo(() => _tenant.LoadCacheForTenantAsync()).MustNotHaveHappened();
        A.CallTo(() => _ckCache.Unload(A<string>._)).MustNotHaveHappened();
    }
}
