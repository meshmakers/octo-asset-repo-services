using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintApplyResultDto"/>. Outcome of an
/// <c>installBlueprint</c> mutation.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintApplyResultDtoType : ObjectGraphType<BlueprintApplyResultDto>
{
    public BlueprintApplyResultDtoType()
    {
        Name = "BlueprintApplyResult";
        Description = "Result of installing a blueprint on a tenant.";

        Field<NonNullGraphType<BooleanGraphType>>("success")
            .Description("True when the install completed without errors.")
            .Resolve(ctx => ctx.Source!.Success);

        Field<NonNullGraphType<StringGraphType>>("tenantId")
            .Description("Tenant the blueprint was applied to.")
            .Resolve(ctx => ctx.Source!.TenantId);

        Field<NonNullGraphType<StringGraphType>>("blueprintId")
            .Description("Fully-qualified blueprint id that was applied.")
            .Resolve(ctx => ctx.Source!.BlueprintId);

        Field<NonNullGraphType<StringGraphType>>("applicationMode")
            .Description("Application mode used: Initial or ReApply.")
            .Resolve(ctx => ctx.Source!.ApplicationMode);

        Field<NonNullGraphType<IntGraphType>>("seedDataFilesApplied")
            .Description("Number of seed-data files imported as part of the apply.")
            .Resolve(ctx => ctx.Source!.SeedDataFilesApplied);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("loadedCkModels")
            .Description("CK model dependencies loaded into the tenant by this apply.")
            .Resolve(ctx => ctx.Source!.LoadedCkModels);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("warnings")
            .Description("Non-blocking warnings produced during the apply.")
            .Resolve(ctx => ctx.Source!.Warnings);
    }
}
