using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the CK method gateway gate (AB#6912).</summary>
public static class CkMethodGatewayServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="ICkMethodGateway" />. It needs <c>AddCkMethodDispatcher()</c> and
    ///     <c>AddCkMethodAuthorization()</c> (evaluator, definition source) in the same service; the resolver that
    ///     generates the per-type method mutations (F3.7) calls it.
    /// </summary>
    public static IServiceCollection AddCkMethodGateway(this IServiceCollection services)
    {
        services.TryAddScoped<ICkMethodGateway, CkMethodGateway>();
        return services;
    }
}
