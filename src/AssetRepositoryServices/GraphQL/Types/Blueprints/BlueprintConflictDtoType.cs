using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintConflictDto"/>. One row of the preview's conflict
/// table. The studio's update dialog reads <c>entityId</c> back as the key when the user
/// supplies a per-entity <c>conflictResolutions</c> override on the apply mutation.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintConflictDtoType : ObjectGraphType<BlueprintConflictDto>
{
    public BlueprintConflictDtoType()
    {
        Name = "BlueprintConflict";
        Description = "Conflict detected by a blueprint update preview against an unlocked or modified tenant entity.";

        Field<NonNullGraphType<StringGraphType>>("entityId")
            .Description("Runtime id of the conflicting tenant entity. Use this verbatim as the key in `conflictResolutions` when overriding.")
            .Resolve(ctx => ctx.Source!.EntityId);

        Field<NonNullGraphType<StringGraphType>>("description")
            .Description("Human-readable description of the conflict.")
            .Resolve(ctx => ctx.Source!.Description);

        Field<StringGraphType>("suggestedResolution")
            .Description("Engine's suggested resolution: KeepUser / KeepBlueprint / Merge / Skip.")
            .Resolve(ctx => ctx.Source!.SuggestedResolution);
    }
}
