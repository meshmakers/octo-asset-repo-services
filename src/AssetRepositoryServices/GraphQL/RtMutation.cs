using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class RtMutation : ObjectGraphType
{
    public RtMutation(IGraphTypesCache graphTypesCache)
    {
        Name = "Runtime";
        
        Field<RtQueryMutation>("RuntimeQuery")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The query runtime id.")
            .Resolve(_ => new RtEntityDto());
        
        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            if (rtEntityDtoType.IsAbstract)
            {
                continue;
            }

            Field($"{rtEntityDtoType.CkTypeId.GetGraphQlCamelCaseName()}s", new RtEntityMutation(graphTypesCache, rtEntityDtoType))
                .Description($"Mutation for entities of type '{rtEntityDtoType.CkTypeId.GetGraphQlPascalCaseName()}'.")
                .Resolve(_ => new RtEntityDto());
        }
    }
}