using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the CK method gateway gate (AB#6912).</summary>
public static class CkMethodGatewayServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="ICkMethodGateway" />. It needs <c>AddCkMethodDispatcher()</c> and
    ///     <c>AddCkMethodAuthorization()</c> (evaluator, definition source) in the same service; the resolver that
    ///     generates the per-type method mutations (F3.7) calls it. It also registers the request-session tripwire of the
    ///     in-process transport (AB#7023, OD-8): an in-process handler never runs with the GraphQL request session, and a
    ///     call that would is refused with <c>METHOD_HANDLER_FAILED</c> before the handler body runs.
    /// </summary>
    public static IServiceCollection AddCkMethodGateway(this IServiceCollection services)
    {
        services.TryAddScoped<ICkMethodGateway, CkMethodGateway>();
        services.AddOctoSessionAmbientStateProbe();
        return services;
    }

    /// <summary>
    ///     Registers <see cref="OctoSessionAmbientStateProbe" /> as in-process ambient state probe, once (idempotent).
    /// </summary>
    internal static IServiceCollection AddOctoSessionAmbientStateProbe(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(ICkInProcessAmbientStateProbe) &&
                               d.ImplementationType == typeof(OctoSessionAmbientStateProbe)))
        {
            services.AddCkInProcessAmbientStateProbe<OctoSessionAmbientStateProbe>();
        }

        return services;
    }
}
