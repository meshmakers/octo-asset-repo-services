using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Services.Swagger;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoSwaggerOptions : IConfigureNamedOptions<OctoSwaggerOptions>
{
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _octoOptions;

    public ConfigureOctoSwaggerOptions(IOptions<OctoAssetRepositoryServicesOptions> octoOptions)
    {
        _octoOptions = octoOptions;
    }

    public void Configure(OctoSwaggerOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OctoSwaggerOptions options)
    {
        options.AuthorityUrl = _octoOptions.Value.Authority.EnsureEndsWith("/");
    }
}
