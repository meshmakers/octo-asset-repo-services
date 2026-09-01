using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

/// <summary>
///     Configures the JWT bearer scheme this service authenticates its API and GraphQL callers with.
/// </summary>
/// <remarks>
///     <para>
///         Extracted from the inline <c>AddJwtBearer(options =&gt; …)</c> delegate in
///         <c>RuntimeEngineBuilderExtensions</c> under AB#5054, for two reasons: it mirrors the shape
///         every other host of <c>TenantAuthorizationMiddleware</c> uses (identity, bot,
///         communication-controller, MCP, AI services), and it makes the settings unit-testable —
///         the <c>AuthenticationType</c> below is load-bearing for a security gate but fails
///         silently, so it needs a test that pins it.
///     </para>
///     <para>
///         It also removes a <c>builder.Services.BuildServiceProvider()</c> call from the delegate:
///         the options are injected now, resolved from the real container.
///     </para>
/// </remarks>
internal class ConfigureJwtBearerOptions(IOptions<OctoAssetRepositoryServicesOptions> assetRepositoryOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        // base-address of your identity server.
        // EnsureEndsWith("/") mirrors what identity / bot / communication-controller
        // do — tokens from IdentityServer carry `iss` with a trailing slash, so
        // ValidIssuer must match the slash-form exactly.
        var authorityUrl = assetRepositoryOptions.Value.Authority.EnsureEndsWith("/");
        options.Authority = authorityUrl;

        options.TokenValidationParameters.ValidateAudience = false;

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;

        // AB#5054 — label the authenticated identity "Bearer" so TenantAuthorizationMiddleware
        // (UseOctoTenantAuthorization(), AB#5032/AB#5047) actually runs its route-tenant vs
        // tenant_id check. The middleware deliberately skips principals whose AuthenticationType
        // is not "Bearer" to avoid false 403s on the cookie/OIDC principals this service also
        // issues for the GraphQL playground — and the JWT handler's default label is
        // "AuthenticationTypes.Federation", not "Bearer", so without this line the whole gate
        // (user path AND the service-token audit log) is a silent no-op on every bearer request.
        // That matters most here: this service runs with ValidateAudience = false, so the tenant
        // match is the only transport-level barrier between a client-credentials client of the
        // authority and a foreign tenant. Same fix as octo-mcp-service (AB#4315) and
        // octo-ai-services (AB#5051).
        // 🔴 Whoever registers the "Bearer" scheme must not replace TokenValidationParameters
        // wholesale afterwards — that silently discards this label again.
        options.TokenValidationParameters.AuthenticationType = JwtBearerDefaults.AuthenticationScheme;
    }
}
