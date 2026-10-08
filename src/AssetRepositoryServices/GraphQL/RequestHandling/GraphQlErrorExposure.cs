using GraphQL.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     AB#6189: decides whether GraphQL error responses carry exception details (the
///     <c>extensions.details</c> entry with the full exception text, inner exceptions and stack trace).
///     Details are exposed only in the Development environment, or when explicitly opted in via the
///     <see cref="ConfigurationKey" /> setting. Error messages, <c>extensions.code</c>/<c>codes</c> and
///     custom extensions (e.g. <c>OctoDetails</c>) are not affected.
/// </summary>
internal static class GraphQlErrorExposure
{
    /// <summary>
    ///     Configuration key that explicitly enables (<c>true</c>) or disables (<c>false</c>) exception details.
    ///     When unset, details are exposed only if the host environment is Development.
    ///     Environment variable: <c>OCTO_GraphQl__ExposeExceptionDetails</c>.
    /// </summary>
    public const string ConfigurationKey = "GraphQl:ExposeExceptionDetails";

    /// <summary>
    ///     Returns true if exception details may be sent to GraphQL clients.
    /// </summary>
    public static bool ShouldExposeExceptionDetails(IHostEnvironment? hostEnvironment, IConfiguration? configuration)
    {
        if (bool.TryParse(configuration?[ConfigurationKey], out var explicitValue))
        {
            return explicitValue;
        }

        return hostEnvironment?.IsDevelopment() == true;
    }

    /// <summary>
    ///     Applies the exposure decision to the GraphQL error info provider options.
    /// </summary>
    public static void Configure(ErrorInfoProviderOptions options, IServiceProvider serviceProvider)
    {
        options.ExposeExceptionDetails = ShouldExposeExceptionDetails(
            serviceProvider.GetService(typeof(IHostEnvironment)) as IHostEnvironment,
            serviceProvider.GetService(typeof(IConfiguration)) as IConfiguration);
    }
}
