using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types.Relay;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.BuilderExtensions;

/// <summary>
///     Builder extension methods for registering core services
/// </summary>
public static class OctoBuilderExtensionsCore
{
    /// <summary>
    ///     Adds the required platform services.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns></returns>
    public static IOctoBuilder AddRequiredPlatformServices(this IOctoBuilder builder)
    {
        builder.Services.AddOptions();
        builder.Services.AddSingleton(
            resolver => resolver.GetRequiredService<IOptions<OctoAssetRepositoryServicesOptions>>().Value);
        builder.Services.AddSingleton(
            resolver => resolver.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value);
        builder.Services.AddSingleton<GraphQLHttpMiddlewareOptions>();
        builder.Services.ConfigureOptions<ConfigureOctoSwaggerOptions>();


        // Add GraphQL types (GraphQL.Relay)
        builder.Services.AddTransient(typeof(ConnectionType<>));
        builder.Services.AddTransient(typeof(EdgeType<>));
        builder.Services.AddTransient<PageInfoType>();

        // GraphQL custom services
        builder.Services.AddSingleton<ISchemaContext, SchemaContext>();

        // Add the basic services of Octo
        builder.Services.AddSingleton<ISystemContext, SystemContext>();
        builder.Services.AddSingleton<IOctoService, OctoService>();
        builder.Services.AddTransient<IUserSchemaService, UserSchemaService>();
        builder.Services.AddTransient<IOctoClientStore, ClientStore>();
        builder.Services.AddTransient<IOctoResourceStore, ResourceStore>();
        builder.Services.AddTransient<IOctoPersistentGrantStore, PersistentGrantStore>();
        builder.Services.AddTransient<IKnownOriginsProvider>(provider => provider.GetRequiredService<IOctoClientStore>());

        return builder;
    }
}
