namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;

/// <summary>
/// Defines how the mapping should be performed.
/// </summary>
public enum MappingMode
{
    /// <summary>
    /// We create new entities, but do not update existing ones.
    /// </summary>
    Insert = 0,

    /// <summary>
    /// We update existing entities, but do not create new ones.
    /// </summary>
    Update = 1,
}