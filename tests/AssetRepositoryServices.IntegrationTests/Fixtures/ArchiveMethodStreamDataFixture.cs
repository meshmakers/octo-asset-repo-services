using System.Security.Claims;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#8598 (F4.3): the archive lifecycle CK methods of System.StreamData against the real host — MongoDB, CrateDB, the
///     stream-data stack of <see cref="StreamDataFixture" />, the real GraphQL schema with the per-type method fields, the real
///     gateway, invocation pipeline and authorization, and the production registration of the handlers
///     (<see cref="ArchiveMethodHost.AddArchiveCkMethodHandlers" />). Nothing is faked.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ArchiveMethodStreamDataFixture : StreamDataFixture
{
    public ArchiveMethodStreamDataFixture()
    {
        Services.AddArchiveCkMethodHandlers(new ConfigurationBuilder().Build());
        Services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [SystemTenantId]);
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // What a service start does after the tenant setup: the import of System.StreamData into the system tenant
        // (Order 12), then the startup validation of the in-process registrations against its CK cache (Order 15).
        var startup = Services
            .Where(d => d.ServiceType == typeof(IAsyncInitializationService) &&
                        (d.ImplementationType == typeof(SystemTenantStreamDataModelImport) ||
                         d.ImplementationType?.Namespace == typeof(CkMethodHandlerOptions).Namespace))
            .Select(d => (IAsyncInitializationService)ActivatorUtilities.CreateInstance(Provider!, d.ImplementationType!))
            .OrderBy(x => x.Order);
        foreach (var service in startup)
        {
            await service.InitializeAsync();
        }
    }

    /// <summary>Executes a GraphQL document in the given tenant (the base fixture always uses the system tenant).</summary>
    public async Task<ExecutionResult> ExecuteGraphQlInTenantAsync(ITenantContext tenantContext, string query,
        ClaimsPrincipal? user)
    {
        var executer = GetService<IDocumentExecuter<OctoSchema>>();
        return await executer.ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = query;
            options.RequestServices = Provider;
            options.UserContext = new GraphQlUserContext(user, tenantContext);
        });
    }
}
