using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintUninstallResultDto"/>. Outcome of an
/// <c>uninstallBlueprint</c> mutation, including the dependents-block path that
/// powers the studio's "you need --cascade" hint.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintUninstallResultDtoType : ObjectGraphType<BlueprintUninstallResultDto>
{
    public BlueprintUninstallResultDtoType()
    {
        Name = "BlueprintUninstallResult";
        Description = "Result of uninstalling a blueprint from a tenant.";

        Field<NonNullGraphType<BooleanGraphType>>("success")
            .Description("True when the uninstall completed.")
            .Resolve(ctx => ctx.Source!.Success);

        Field<StringGraphType>("uninstalledBlueprintId")
            .Description("Fully-qualified id of the blueprint that was uninstalled, if any.")
            .Resolve(ctx => ctx.Source!.UninstalledBlueprintId);

        Field<NonNullGraphType<IntGraphType>>("entitiesDeleted")
            .Description("Number of locked entities erased from the tenant.")
            .Resolve(ctx => ctx.Source!.EntitiesDeleted);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("cascadedDependencies")
            .Description("Blueprint ids that were cascade-uninstalled alongside the target.")
            .Resolve(ctx => ctx.Source!.CascadedDependencies);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("blockingDependents")
            .Description("Other installed blueprints that still depend on the target. Populated when uninstall is refused because cascade was not requested.")
            .Resolve(ctx => ctx.Source!.BlockingDependents);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("warnings")
            .Description("Non-blocking warnings produced during the uninstall.")
            .Resolve(ctx => ctx.Source!.Warnings);
    }
}
