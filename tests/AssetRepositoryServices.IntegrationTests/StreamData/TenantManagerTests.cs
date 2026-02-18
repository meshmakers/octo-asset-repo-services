using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

public class TenantManagerTests
{
    private const string TenantId = "test-tenant";

    private readonly ISystemContext _systemContext;
    private readonly IStreamDataTenantContextFactory _contextFactory;
    private readonly TenantManager _tenantManager;

    public TenantManagerTests()
    {
        var logger = NullLogger<TenantManager>.Instance;
        _systemContext = A.Fake<ISystemContext>();
        _contextFactory = A.Fake<IStreamDataTenantContextFactory>();
        _tenantManager = new TenantManager(logger, _systemContext, _contextFactory);
    }

    private void SetupTenantContext(StreamDataGlobalSettings? settings)
    {
        var tenantContext = A.Fake<ITenantContext>();
        var session = A.Fake<IOctoAdminSession>();

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId))
            .Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetAdminSessionAsync())
            .Returns(Task.FromResult(session));
        A.CallTo(() => tenantContext.GetConfigurationAsync<StreamDataGlobalSettings>(
                session, Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Constants.StreamDataEnabledKey, A<StreamDataGlobalSettings?>._))
            .Returns(Task.FromResult(settings));
    }

    private void SetupContextFactoryReturnsStartableContext()
    {
        var streamDataTenantContext = A.Fake<IStreamDataTenantContext>();
        A.CallTo(() => streamDataTenantContext.StartAsync()).Returns(Task.FromResult(true));
        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .Returns(Task.FromResult(streamDataTenantContext));
    }

    [Fact]
    public async Task EnableStreamData_AlreadyEnabledAndStarted_ReturnsImmediately()
    {
        // First enable and start the tenant so it's in _streamDataTenantContexts
        SetupTenantContext(null);
        SetupContextFactoryReturnsStartableContext();
        await _tenantManager.EnableStreamData(TenantId);

        // Reset call tracking
        Fake.ClearRecordedCalls(_systemContext);
        Fake.ClearRecordedCalls(_contextFactory);

        // Second call should return immediately without DB access
        await _tenantManager.EnableStreamData(TenantId);

        A.CallTo(() => _systemContext.FindTenantContextAsync(A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _contextFactory.CreateAsync(A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task EnableStreamData_NeverEnabled_EnablesConfigAndStartsTenant()
    {
        // Config returns null (never enabled)
        SetupTenantContext(null);
        SetupContextFactoryReturnsStartableContext();

        await _tenantManager.EnableStreamData(TenantId);

        // Should have called CreateAsync to create the context
        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .MustHaveHappenedOnceExactly();

        // Verify the tenant is now tracked (GetStreamDataTenantContext returns non-null)
        var context = _tenantManager.GetStreamDataTenantContext(TenantId);
        Assert.NotNull(context);
    }

    [Fact]
    public async Task EnableStreamData_ConfigDisabled_EnablesConfigAndStartsTenant()
    {
        SetupTenantContext(StreamDataGlobalSettings.Disabled);
        SetupContextFactoryReturnsStartableContext();

        await _tenantManager.EnableStreamData(TenantId);

        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .MustHaveHappenedOnceExactly();

        var context = _tenantManager.GetStreamDataTenantContext(TenantId);
        Assert.NotNull(context);
    }

    [Fact]
    public async Task EnableStreamData_ConfigEnabledButNotStarted_StartsTenant()
    {
        // This is the restore scenario: config says enabled, but no in-memory context
        SetupTenantContext(StreamDataGlobalSettings.Enabled);
        SetupContextFactoryReturnsStartableContext();

        await _tenantManager.EnableStreamData(TenantId);

        // Should have called CreateAsync to start the tenant
        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .MustHaveHappenedOnceExactly();

        // Verify the tenant is now tracked
        var context = _tenantManager.GetStreamDataTenantContext(TenantId);
        Assert.NotNull(context);
    }

    [Fact]
    public async Task StartTenantAsync_AlreadyStarted_DoesNotCreateAgain()
    {
        SetupTenantContext(null);
        SetupContextFactoryReturnsStartableContext();

        // Start once
        await _tenantManager.StartTenantAsync(TenantId);

        Fake.ClearRecordedCalls(_contextFactory);

        // Start again
        await _tenantManager.StartTenantAsync(TenantId);

        A.CallTo(() => _contextFactory.CreateAsync(A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StartTenantAsync_ContextStartFails_DoesNotTrackTenant()
    {
        var streamDataTenantContext = A.Fake<IStreamDataTenantContext>();
        A.CallTo(() => streamDataTenantContext.StartAsync()).Returns(Task.FromResult(false));
        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .Returns(Task.FromResult(streamDataTenantContext));

        await _tenantManager.StartTenantAsync(TenantId);

        var context = _tenantManager.GetStreamDataTenantContext(TenantId);
        Assert.Null(context);
    }

    [Fact]
    public async Task StopTenantAsync_RunningTenant_StopsAndRemoves()
    {
        SetupTenantContext(null);
        SetupContextFactoryReturnsStartableContext();

        await _tenantManager.StartTenantAsync(TenantId);
        Assert.NotNull(_tenantManager.GetStreamDataTenantContext(TenantId));

        await _tenantManager.StopTenantAsync(TenantId);
        Assert.Null(_tenantManager.GetStreamDataTenantContext(TenantId));
    }

    [Fact]
    public async Task StopTenantAsync_NotRunning_DoesNothing()
    {
        // Should not throw
        await _tenantManager.StopTenantAsync(TenantId);

        Assert.Null(_tenantManager.GetStreamDataTenantContext(TenantId));
    }

    [Fact]
    public async Task DisableStreamData_RunningTenant_DisablesConfigAndStops()
    {
        SetupTenantContext(null);
        SetupContextFactoryReturnsStartableContext();

        // Start the tenant first
        await _tenantManager.StartTenantAsync(TenantId);
        Assert.NotNull(_tenantManager.GetStreamDataTenantContext(TenantId));

        // Disable
        await _tenantManager.DisableStreamDataAsync(TenantId);
        Assert.Null(_tenantManager.GetStreamDataTenantContext(TenantId));
    }

    [Fact]
    public async Task DeleteTenantAsync_RunningTenant_DeletesAndRemoves()
    {
        SetupTenantContext(null);
        var streamDataTenantContext = A.Fake<IStreamDataTenantContext>();
        A.CallTo(() => streamDataTenantContext.StartAsync()).Returns(Task.FromResult(true));
        A.CallTo(() => _contextFactory.CreateAsync(TenantId))
            .Returns(Task.FromResult(streamDataTenantContext));

        await _tenantManager.StartTenantAsync(TenantId);
        Assert.NotNull(_tenantManager.GetStreamDataTenantContext(TenantId));

        await _tenantManager.DeleteTenantAsync(TenantId);

        Assert.Null(_tenantManager.GetStreamDataTenantContext(TenantId));
        A.CallTo(() => streamDataTenantContext.DeleteAsync())
            .MustHaveHappenedOnceExactly();
    }
}
