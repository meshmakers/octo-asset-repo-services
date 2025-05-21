using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements an Octo query, based on a given data source
/// </summary>
[DoNotRegister]
internal sealed class OctoQuery : ObjectGraphType
{
    public OctoQuery(IGraphTypesCache graphTypesCache)
    {
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