using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData;


internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStreamDataManagement(this IServiceCollection services)
    {
        services.AddSingleton<ITenantManager, TenantManager>();
        services.AddSingleton<IStreamDataTenantContextFactory, StreamDataTenantContextFactory>();

        services.AddTransient<IStreamDataDatabaseManager, StreamDataDatabaseManager>();
        return services;
    }
}