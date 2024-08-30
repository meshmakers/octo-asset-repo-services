using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Services.Common.StreamData.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

/// <summary>
/// 
/// </summary>
public class ConfigureStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
{
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="options"></param>
    public ConfigureStreamDataConfiguration(IOptions<OctoAssetRepositoryServicesOptions> options)
    {
        _options = options;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public void Configure(StreamDataConfiguration options)
    {
        Configure(Options.DefaultName, options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="options"></param>
    public void Configure(string? name, StreamDataConfiguration options)
    {
        var o = _options.Value;
        options.ConnectionStringFromConfiguration(
            o.StreamDataHost,
            o.StreamDataUser,
            o.StreamDataPassword);
    }
}