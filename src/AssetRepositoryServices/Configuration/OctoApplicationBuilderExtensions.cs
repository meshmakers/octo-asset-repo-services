using GraphQL.Server.Ui.Altair;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.HttpOverrides;

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
    public static IApplicationBuilder UseOctoAssetRepositoryServices(
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
        app.UseOctoApiVersioningAndDocumentation();

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
            endpoints.MapGraphQlTenantPlayground(new AltairOptions
                {
//                    RequestCredentials = RequestCredentials.Include,
                    GraphQLEndPoint = "/tenants/{tenantId}/graphQl"
                }, "tenants/{tenantId:tenantId}/graphQl/playground")
                .RequireAuthorization(AssetRepositoryServiceConstants.AuthenticatedUserPolicy);
            endpoints.MapGraphQL<GraphQlTenantMiddleware>(
                "tenants/{tenantId:tenantId}/graphQl"); //.RequireAuthorization(AssetRepositoryServiceConstants.TenantApiReadWritePolicy);// TODO enable again!
        });
    }
}