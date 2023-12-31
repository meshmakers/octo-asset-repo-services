using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions : IConfigureNamedOptions<DistributionEventHubOptions>
{
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _assetRepoServiceOptions;
    private readonly IOptions<OctoSystemConfiguration> _octoSystemConfiguration;

    public ConfigureDistributionEventHubOptions(IOptions<OctoAssetRepositoryServicesOptions> assetRepoServiceOptions,
        IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    {
        _assetRepoServiceOptions = assetRepoServiceOptions;
        _octoSystemConfiguration = octoSystemConfiguration;
    }


    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.BrokerHost = _assetRepoServiceOptions.Value.BrokerHost;
        options.BrokerUser = _assetRepoServiceOptions.Value.BrokerUser;
        options.BrokerPassword = _assetRepoServiceOptions.Value.BrokerPassword;
        options.RepositoryHost = _octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = _octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = _octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = _octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}