using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal sealed class RtMutation : ObjectGraphType
{
    public RtMutation(IGraphTypesCache graphTypesCache)
    {
        Name = "Runtime";
        
        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            if (rtEntityDtoType.IsAbstract)
            {
                continue;
            }

            Field($"{rtEntityDtoType.CkTypeId.GetGraphQlName()}s", new RtEntityMutation(graphTypesCache, rtEntityDtoType))
                .Description($"Mutation for entities of type '{rtEntityDtoType.CkTypeId.GetGraphQlName()}'.")
                .Resolve(_ => new RtEntityDto());
        }
    }
}