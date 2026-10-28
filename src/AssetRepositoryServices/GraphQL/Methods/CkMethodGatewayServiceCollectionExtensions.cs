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
        // The resolver forwards the raw bearer token of the request to the handler (AB#7026).
        services.AddHttpContextAccessor();
        services.AddOctoSessionAmbientStateProbe();
        return services;
    }

    /// <summary>
    ///     Registers the CK methods that the asset repository owns itself as in-process handlers (AB#6893): the list is
    ///     <see cref="GatewayOwnedMethodHandlers.Register" />. The dispatcher of <c>AddCkMethodDispatcher()</c> prefers an
    ///     in-process handler over the bus, so a gateway-owned method is never published and never announced as a remote
    ///     handler; every call, in-process or not, runs through the one invocation pipeline (audit, authorization,
    ///     idempotency) and the host evaluates the declared authorization of the method before the handler body. Needs
    ///     <c>AddCkMethodAuthorization()</c>; it also registers the session tripwire (<c>AddCkMethodGateway()</c> does too,
    ///     once). <c>AddCkMethodCallerTokenValidation()</c> is deliberately <b>not</b> needed: it validates the forwarded token
    ///     of a call that arrives over the bus at a remote handler host, and the asset repository hosts no remote handler;
    ///     in-process the principal comes from the gateway, which has authenticated the HTTP request.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="additional">More handlers; tests add their sample here, production code leaves it out.</param>
    public static IServiceCollection AddGatewayOwnedCkMethodHandlers(this IServiceCollection services,
        Action<ICkMethodHandlerRegistrationBuilder>? additional = null)
    {
        services.AddCkInProcessMethodHandlers(methods =>
        {
            GatewayOwnedMethodHandlers.Register(methods);
            additional?.Invoke(methods);
        });
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
