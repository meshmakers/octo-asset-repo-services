using System;
using GraphQL.Server.Ui.Playground;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;

internal static class GraphQlTenantBuilder
{
    internal static PlaygroundTenantEndpointConventionBuilder MapGraphQlTenantPlayground(
        this IEndpointRouteBuilder endpoints, PlaygroundOptions options, string pattern = "ui/playground")
    {
        if (endpoints == null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }

        var requestDelegate = endpoints.CreateApplicationBuilder()
            .UseMiddleware<PlaygroundTenantMiddleware>(options ?? new PlaygroundOptions()).Build();
        return new PlaygroundTenantEndpointConventionBuilder(endpoints.Map(pattern, requestDelegate)
            .WithDisplayName("GraphQL Playground"));
    }

    internal class PlaygroundTenantEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly IEndpointConventionBuilder _builder;

        internal PlaygroundTenantEndpointConventionBuilder(IEndpointConventionBuilder builder)
        {
            _builder = builder;
        }

        /// <inheritdoc />
        public void Add(Action<EndpointBuilder> convention)
        {
            _builder.Add(convention);
        }
    }
}
