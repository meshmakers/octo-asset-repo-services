using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements mutations of Octo
/// </summary>
[DoNotRegister]
internal sealed class OctoMutation : ObjectGraphType
{
    public OctoMutation(IGraphTypesCache graphTypesCache)
    {
        Field("Runtime", new RtMutation(graphTypesCache))
            .Resolve(_ => new RtEntityDto());
        
        Field<CkMutation>("ConstructionKit")
            .Resolve(_ => new object());
    }
}