using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#8722: one asset-repository replica - its own service provider, so its own CK cache, model loader and import guard
///     state - on the system tenant of another fixture (same MongoDB, same system database). Several instances on one
///     system tenant behave like pods of one deployment that start at the same time.
/// </summary>
public sealed class AssetRepoReplicaFixture : DatabaseFixture
{
    private readonly string _systemTenantId;

    /// <param name="systemTenantId">The system tenant the replica runs on (created by another fixture).</param>
    /// <param name="systemDatabaseName">Its database.</param>
    /// <param name="configureMethods">Additional in-process registrations, e.g. one for a method the model does not have.</param>
    public AssetRepoReplicaFixture(string systemTenantId, string systemDatabaseName,
        Action<ICkMethodHandlerRegistrationBuilder>? configureMethods = null)
    {
        _systemTenantId = systemTenantId;

        // DatabaseFixture configures its own GUID-named system tenant when it initializes; PostConfigure runs after it.
        Services.PostConfigure<OctoSystemConfiguration>(o =>
        {
            o.SystemTenantId = systemTenantId;
            o.SystemDatabaseName = systemDatabaseName;
        });

        // The production registration of the archive handlers and of the System.StreamData import (Program.cs).
        Services.AddArchiveCkMethodHandlers(new ConfigurationBuilder().Build());
        if (configureMethods != null)
        {
            Services.AddCkInProcessMethodHandlers(configureMethods);
        }
    }

    /// <summary>
    ///     What <c>HostedInitializer</c> runs at a service start for the methods, in the same order: the import of
    ///     System.StreamData into the system tenant (Order 12), then the validation of the in-process registrations against
    ///     the CK cache of the system tenant (Order 15, the default validation tenant). Throws what the start would throw.
    /// </summary>
    public async Task StartAsync()
    {
        EnsureInitialized();

        var startup = Services
            .Where(d => d.ServiceType == typeof(IAsyncInitializationService) &&
                        (d.ImplementationType == typeof(SystemTenantStreamDataModelImport) ||
                         d.ImplementationType?.Namespace == typeof(CkMethodHandlerOptions).Namespace))
            .Select(d => (IAsyncInitializationService)ActivatorUtilities.CreateInstance(Provider!, d.ImplementationType!))
            .OrderBy(s => s.Order)
            .ToList();
        foreach (var service in startup)
        {
            await service.InitializeAsync();
        }
    }

    /// <summary>Loads the CK cache of the system tenant now, as a request or a tenant setup of the running replica does.</summary>
    public async Task LoadSystemTenantCkCacheAsync()
    {
        var systemContext = GetSystemContext();
        var tenant = await systemContext.FindTenantContextAsync(_systemTenantId);
        await tenant.LoadCacheForTenantAsync();
    }
}
