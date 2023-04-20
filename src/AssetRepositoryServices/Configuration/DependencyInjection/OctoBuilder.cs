using System;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection;

/// <summary>
///     IdentityServer helper class for DI configuration
/// </summary>
public class OctoBuilder : IOctoBuilder
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="OctoBuilder" /> class.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <exception cref="System.ArgumentNullException">services</exception>
    public OctoBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    ///     Gets the services.
    /// </summary>
    /// <value>
    ///     The services.
    /// </value>
    public IServiceCollection Services { get; }
}
