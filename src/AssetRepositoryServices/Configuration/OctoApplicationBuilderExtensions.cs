using System;
using GraphQL.Server.Ui.Playground;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.Swagger.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>
///     Pipeline extension methods for adding Octo
/// </summary>
public static class OctoApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds Octo to the pipeline.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOcto(
        this IApplicationBuilder app)
    {
        if (app == null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        ConfigureOcto(app);
        return app;
    }

    private static void ConfigureOcto(IApplicationBuilder app)
    {
        app.UseOctoPersistence();
        app.UseOctoApiVersioningAndDocumentation();

        var scopeFactory = app.ApplicationServices.GetRequiredService<IServiceScopeFactory>();
        using (var scope = scopeFactory.CreateScope())
        {
            var userSchemaService = scope.ServiceProvider.GetRequiredService<IUserSchemaService>();
            userSchemaService.SetupAsync().GetAwaiter().GetResult();
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto
        });

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        // this is required for websockets support
        app.UseWebSockets();

        // Because we are behind a load balancer using HTTP it is needed to use XForwardProto to ensure
        // that requests are send by HTTPS (e. g. Authentication to Identity Server)
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto
        });

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapGraphQlTenantPlayground(new PlaygroundOptions
                {
                    RequestCredentials = RequestCredentials.Include,
                    GraphQLEndPoint = "/tenants/{tenantId}/graphQl"
                }, "tenants/{tenantId:tenantId}/graphQl/playground")
                .RequireAuthorization(AssetRepositoryServiceConstants.AuthenticatedUserPolicy);
            endpoints.MapGraphQL<GraphQlTenantMiddleware>(
                "tenants/{tenantId:tenantId}/graphQl"); //.RequireAuthorization(AssetRepositoryServiceConstants.TenantApiReadWritePolicy);// TODO enable again!
        });
    }
}
