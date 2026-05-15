using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintUpdateInfoDto"/>. Available-updates summary used
/// to drive the studio's "update available" badge and the version picker in the update dialog.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintUpdateInfoDtoType : ObjectGraphType<BlueprintUpdateInfoDto>
{
    public BlueprintUpdateInfoDtoType()
    {
        Name = "BlueprintUpdateInfo";
        Description = "Available updates for the tenant's currently installed blueprint.";

        Field<StringGraphType>("currentBlueprintId")
            .Description("Fully-qualified blueprint id currently installed on the tenant.")
            .Resolve(ctx => ctx.Source!.CurrentBlueprintId);

        Field<StringGraphType>("currentVersion")
            .Description("SemVer of the currently installed version.")
            .Resolve(ctx => ctx.Source!.CurrentVersion);

        Field<StringGraphType>("recommendedVersion")
            .Description("Fully-qualified id of the recommended target version, when an update is available.")
            .Resolve(ctx => ctx.Source!.RecommendedVersion);

        Field<NonNullGraphType<BooleanGraphType>>("hasUpdate")
            .Description("True when at least one newer version is available in the catalog.")
            .Resolve(ctx => ctx.Source!.HasUpdate);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("availableVersions")
            .Description("All catalog versions reachable from the current installation, including downgrades.")
            .Resolve(ctx => ctx.Source!.AvailableVersions);
    }
}
