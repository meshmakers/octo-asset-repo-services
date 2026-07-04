using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintHistoryItemDto"/>. One audit-log entry of a
/// blueprint operation (initial apply, re-apply, update, rollback, uninstall) on the tenant.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintHistoryItemDtoType : ObjectGraphType<BlueprintHistoryItemDto>
{
    public BlueprintHistoryItemDtoType()
    {
        Name = "BlueprintHistoryItem";
        Description = "Audit-log entry of a blueprint operation against the tenant.";

        Field<NonNullGraphType<StringGraphType>>("blueprintId")
            .Description("Fully-qualified blueprint id that was applied.")
            .Resolve(ctx => ctx.Source!.BlueprintId);

        Field<NonNullGraphType<DateTimeGraphType>>("appliedAt")
            .Description("UTC timestamp of the operation.")
            .Resolve(ctx => ctx.Source!.AppliedAt);

        Field<NonNullGraphType<StringGraphType>>("applicationMode")
            .Description("Application mode: Initial / Update / Migration / ReApply.")
            .Resolve(ctx => ctx.Source!.ApplicationMode);

        Field<StringGraphType>("previousVersion")
            .Description("Fully-qualified id of the prior version, when this entry is an update.")
            .Resolve(ctx => ctx.Source!.PreviousVersion);

        Field<NonNullGraphType<IntGraphType>>("entitiesCreated")
            .Description("Number of entities created by this operation.")
            .Resolve(ctx => ctx.Source!.EntitiesCreated);

        Field<NonNullGraphType<IntGraphType>>("entitiesUpdated")
            .Description("Number of entities updated by this operation.")
            .Resolve(ctx => ctx.Source!.EntitiesUpdated);

        Field<NonNullGraphType<IntGraphType>>("entitiesDeleted")
            .Description("Number of entities deleted by this operation.")
            .Resolve(ctx => ctx.Source!.EntitiesDeleted);

        Field<StringGraphType>("seedDataChecksum")
            .Description("Optional checksum of the seed data that was applied.")
            .Resolve(ctx => ctx.Source!.SeedDataChecksum);
    }
}
