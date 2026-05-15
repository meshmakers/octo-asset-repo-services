using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL enum mirror of <see cref="ConflictResolution"/>. Per-entity override for the
/// preview's suggested resolution when applying an update.
/// </summary>
internal sealed class ConflictResolutionEnumType : EnumerationGraphType<ConflictResolution>
{
    public ConflictResolutionEnumType()
    {
        Name = "BlueprintConflictResolution";
        Description = "Per-entity override for an unlocked-conflict during a blueprint update.";
    }
}
