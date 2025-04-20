using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements an Octo query, based on a given data source
/// </summary>
[DoNotRegister]
internal sealed class OctoQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;

    public OctoQuery(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache)
    {
        _options = options;
        Name = "OctoQuery";

        Field<CkQuery>("ConstructionKit")
            .Resolve(_ => new object());

        Field("Runtime", new RuntimeModelQuery(graphTypesCache))
            .Resolve(_ => new RtEntityDto());

        
        if (graphTypesCache.GetStreamTypes().Length != 0)
        {
            // make sure to only add the stream data field if there are stream types.
            Field("StreamData", new StreamDataQuery(graphTypesCache))
                .Resolve(_ => new StreamDataEntityDto());
        }
    }
}