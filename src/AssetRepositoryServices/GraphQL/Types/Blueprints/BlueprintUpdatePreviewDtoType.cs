using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintUpdatePreviewDto"/>. The "what would change"
/// summary returned by <c>previewUpdate</c> — adds/updates/deletes counts, warnings, and
/// the per-entity conflict list the studio uses to drive its resolution UI.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintUpdatePreviewDtoType : ObjectGraphType<BlueprintUpdatePreviewDto>
{
    public BlueprintUpdatePreviewDtoType()
    {
        Name = "BlueprintUpdatePreview";
        Description = "Diff of a planned blueprint update — counts and conflicts, no side effects.";

        Field<NonNullGraphType<StringGraphType>>("targetVersion")
            .Description("Fully-qualified id of the target blueprint version.")
            .Resolve(ctx => ctx.Source!.TargetVersion);

        Field<NonNullGraphType<IntGraphType>>("entitiesToAdd")
            .Description("Number of entities the update would add.")
            .Resolve(ctx => ctx.Source!.EntitiesToAdd);

        Field<NonNullGraphType<IntGraphType>>("entitiesToUpdate")
            .Description("Number of entities the update would upsert.")
            .Resolve(ctx => ctx.Source!.EntitiesToUpdate);

        Field<NonNullGraphType<IntGraphType>>("entitiesToDelete")
            .Description("Number of entities the update would delete (Full mode only).")
            .Resolve(ctx => ctx.Source!.EntitiesToDelete);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintConflictDtoType>>>>("conflicts")
            .Description("Per-entity conflicts the studio needs to resolve before the apply.")
            .Resolve(ctx => ctx.Source!.Conflicts);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("warnings")
            .Description("Non-blocking warnings reported by the diff (e.g. mode-specific notices).")
            .Resolve(ctx => ctx.Source!.Warnings);
    }
}
