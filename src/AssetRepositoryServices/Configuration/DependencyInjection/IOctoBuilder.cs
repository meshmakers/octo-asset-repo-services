// ReSharper disable once CheckNamespace

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection;

/// <summary>
///     Octo builder Interface
/// </summary>
public interface IOctoBuilder
{
    /// <summary>
    ///     Gets the services.
    /// </summary>
    /// <value>
    ///     The services.
    /// </value>
    IServiceCollection Services { get; }
}
