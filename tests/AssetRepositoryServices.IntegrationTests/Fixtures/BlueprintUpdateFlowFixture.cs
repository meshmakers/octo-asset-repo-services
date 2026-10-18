using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for the blueprint update flow tests (AB#6315): real blueprint service and MongoDB, with a
/// local file system blueprint catalog pointing at <c>TestBlueprints/</c> in the output directory as the
/// only catalog (no GitHub catalogs, so no external state leaks in). Each test creates and drops its own
/// child tenant because an update changes the installed version of the tenant.
/// </summary>
public class BlueprintUpdateFlowFixture : AssetRepoFixture
{
    public const string TestBlueprintsRelativePath = "TestBlueprints";

    protected override async Task InitializeServicesAsync()
    {
        // The Mongo repositories and the blueprint support come from ServiceCollectionFixture
        // (AddOctoAssetRepositoryServices). No second AddRuntimeEngine() here: it would put the in-memory
        // repository provider on top of the MongoDB one, and the blueprint service needs the real tenant repositories.

        // AssetRepositoryServices replaces the engine's logging notifications with the event-hub bridge, which
        // publishes to RabbitMQ. CI has no broker (a developer machine usually has one, which hides this), so a
        // blueprint apply would wait forever. Tests do not need the notifications.
        Services.Replace(ServiceDescriptor.Singleton<IBlueprintNotifications, NoOpBlueprintNotifications>());

        // AB#6871: the flow tests exercise the real blueprint service and database; what the host's tenant upgrade
        // guard says about the CK update set is switched per test (the guard itself is covered by its own tests).
        Services.AddSingleton<SwitchableBlueprintCkUpgradeGuard>();
        Services.Replace(ServiceDescriptor.Singleton<IBlueprintCkUpgradeGuard>(
            sp => sp.GetRequiredService<SwitchableBlueprintCkUpgradeGuard>()));

        Services.RemoveAll<IBlueprintCatalog>();
        Services.AddTransient<IBlueprintCatalog, LocalFileSystemBlueprintCatalog>();

        // Fixture-local cache directory: the default one is shared by every test process on the machine.
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "TestBlueprintsCache",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        Services.Configure<LocalFileSystemBlueprintCatalogOptions>(options =>
        {
            options.RootPath = Path.Combine(AppContext.BaseDirectory, TestBlueprintsRelativePath);
            options.IsEnabled = true;
            options.CacheDirectory = cacheDir;
        });

        await base.InitializeServicesAsync();
    }

    public IBlueprintService GetBlueprintService() => GetService<IBlueprintService>();

    public SwitchableBlueprintCkUpgradeGuard CkUpgradeGuard => GetService<SwitchableBlueprintCkUpgradeGuard>();

    public ITenantBlueprintHistory GetBlueprintHistory() => GetService<ITenantBlueprintHistory>();

    public ITenantBlueprintInstallations GetBlueprintInstallations() => GetService<ITenantBlueprintInstallations>();

    public IRuntimeRepositoryProvider GetRuntimeRepositoryProvider() => GetService<IRuntimeRepositoryProvider>();

    /// <summary>
    /// Creates a fresh child tenant. The blueprint's CK model dependencies are installed on apply. The tenant
    /// id doubles as database name and must stay short (MongoDB application name limit).
    /// </summary>
    public async Task<string> CreateTenantAsync(string prefix)
    {
        var tenantId = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(26, prefix.Length + 9)];
        var systemContext = GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
        return tenantId;
    }

    public async Task DropTenantAsync(string tenantId)
    {
        try
        {
            using var session = await GetSystemContext().GetAdminSessionAsync();
            session.StartTransaction();
            await GetSystemContext().DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

/// <summary>
/// Swallows blueprint notifications so the flow tests never depend on a message broker.
/// </summary>
internal sealed class NoOpBlueprintNotifications : IBlueprintNotifications
{
    public Task NotifyAppliedAsync(BlueprintAppliedNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyUpdatedAsync(BlueprintUpdatedNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyUninstalledAsync(BlueprintUninstalledNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyOperationFailedAsync(BlueprintOperationFailedNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// AB#6871: stands in for the host's tenant upgrade guard in the flow tests - answers "nothing breaks" unless a test
/// sets blockers - and records what it was asked to judge.
/// </summary>
public sealed class SwitchableBlueprintCkUpgradeGuard : IBlueprintCkUpgradeGuard
{
    private readonly object _gate = new();
    private List<BlueprintCkUpgradeBlocker> _blockers = [];
    private readonly List<List<string>> _asked = [];

    public void Block(params BlueprintCkUpgradeBlocker[] blockers)
    {
        lock (_gate)
        {
            _blockers = blockers.ToList();
            _asked.Clear();
        }
    }

    public IReadOnlyList<List<string>> Asked
    {
        get
        {
            lock (_gate)
            {
                return _asked.Select(a => a.ToList()).ToList();
            }
        }
    }

    public Task<IReadOnlyList<BlueprintCkUpgradeBlocker>> EvaluateAsync(string tenantId,
        IReadOnlyList<Meshmakers.Octo.ConstructionKit.Contracts.CkModelId> updateSet,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _asked.Add(updateSet.Select(m => m.FullName).ToList());
            return Task.FromResult<IReadOnlyList<BlueprintCkUpgradeBlocker>>(_blockers.ToList());
        }
    }
}
