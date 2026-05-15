using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintInstallationDto"/>. One row of the tenant's
/// currently installed blueprints view — distinct from the append-only history (which records
/// every install / update / rollback / uninstall operation).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintInstallationDtoType : ObjectGraphType<BlueprintInstallationDto>
{
    public BlueprintInstallationDtoType()
    {
        Name = "BlueprintInstallation";
        Description = "Blueprint currently installed on the tenant.";

        Field<NonNullGraphType<StringGraphType>>("blueprintId")
            .Description("Fully-qualified blueprint id (Name-Version).")
            .Resolve(ctx => ctx.Source!.BlueprintId);

        Field<NonNullGraphType<DateTimeGraphType>>("installedAt")
            .Description("UTC timestamp of the initial install on this tenant.")
            .Resolve(ctx => ctx.Source!.InstalledAt);

        Field<NonNullGraphType<DateTimeGraphType>>("lastUpdatedAt")
            .Description("UTC timestamp of the most recent update or re-apply touching this row.")
            .Resolve(ctx => ctx.Source!.LastUpdatedAt);

        Field<NonNullGraphType<BooleanGraphType>>("isDependency")
            .Description("True when this row was originally pulled in as a transitive dependency of another blueprint.")
            .Resolve(ctx => ctx.Source!.IsDependency);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("resolvedDependencies")
            .Description("Blueprint ids that were resolved as transitive dependencies of this row.")
            .Resolve(ctx => ctx.Source!.ResolvedDependencies);

        Field<StringGraphType>("seedDataChecksum")
            .Description("Optional checksum of the seed data that was applied to this row.")
            .Resolve(ctx => ctx.Source!.SeedDataChecksum);
    }
}
