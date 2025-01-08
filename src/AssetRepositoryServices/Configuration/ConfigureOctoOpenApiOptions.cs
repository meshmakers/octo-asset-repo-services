using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoOpenApiOptions(IOptions<OctoAssetRepositoryServicesOptions> octoOptions)
    : IConfigureNamedOptions<OctoOpenApiOptions>
{
    public void Configure(OctoOpenApiOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OctoOpenApiOptions options)
    {
        options.AuthorityUrl = octoOptions.Value.Authority.EnsureEndsWith("/");
    }
}