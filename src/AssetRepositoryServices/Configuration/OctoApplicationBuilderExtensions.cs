using GraphQL.Server.Ui.Altair;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
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
        this WebApplication app)
    {
        if (app == null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        ConfigureOcto(app);
        return app;
    }

    private static void ConfigureOcto(WebApplication app)
    {
        app.UseOctoApiVersioningAndDocumentation();

        // Because we are behind a load balancer using HTTP, it is necessary to use XForwardProto to ensure
        // that requests are sent by HTTPS (e.g., Authentication to Identity Server)
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
        };
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeadersOptions);

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOctoTenantAuthorization();

        // this is required for websockets support
        app.UseWebSockets();

        app.MapControllers();
        app.MapGraphQlTenantPlayground(new AltairOptions
            {
//                    RequestCredentials = RequestCredentials.Include,
                GraphQLEndPoint = "/tenants/{tenantId}/graphQl"
            }, "tenants/{tenantId:tenantId}/graphQl/playground")
            .RequireAuthorization(AssetRepositoryServiceConstants.AuthenticatedUserPolicy);
        // AB#6171: GraphQL multipart uploads (BinaryLinked) accept the per-file upload limit of the file
        // system plus multipart overhead, instead of Kestrel's 30 MB default.
        var maxUploadBytes = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.FilesOptions>>().Value.MaxUploadBytes;
        app.MapGraphQL<OctoSchema>("tenants/{tenantId:tenantId}/graphQl", c =>
            {
                c.ReadFormOnPost = true;
            }).RequireAuthorization(AssetRepositoryServiceConstants.AuthenticatedUserPolicyGraphApi)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(maxUploadBytes + 1024 * 1024));
    }
}