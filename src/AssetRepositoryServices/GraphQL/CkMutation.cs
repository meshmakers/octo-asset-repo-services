using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal class CkMutation : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CkMutation(IGraphTypesCache graphTypesCache)
    {
        Name = "ConstructionKit";
        
        
    }
}