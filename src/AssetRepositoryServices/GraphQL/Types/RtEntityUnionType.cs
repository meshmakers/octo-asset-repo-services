using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     A GraphQL Union type for runtime entities that can be of multiple CK types.
///     Used for associations where the target can be any of several derived types.
/// </summary>
[DoNotRegister]
internal class RtEntityUnionType : UnionGraphType
{
    /// <summary>
    ///     Creates a new union type containing the specified entity types.
    /// </summary>
    /// <param name="name">The GraphQL name for this union</param>
    /// <param name="description">Description of the union</param>
    /// <param name="entityTypes">The entity types that are part of this union</param>
    public RtEntityUnionType(string name, string description, IEnumerable<RtEntityDtoType> entityTypes)
    {
        Name = name;
        Description = description;
        var entityTypes1 = entityTypes.ToList();

        // Register all entity types as possible types
        foreach (var entityType in entityTypes1)
        {
            AddPossibleType(entityType);
        }

        // Set the ResolveType function to determine which type a given object is
        ResolveType = obj =>
        {
            if (obj is RtEntityDto rtEntityDto)
            {
                return entityTypes1.FirstOrDefault(t =>
                    t.CkTypeId.ToRtCkId() == rtEntityDto.CkTypeId);
            }
            return null;
        };
    }
}
