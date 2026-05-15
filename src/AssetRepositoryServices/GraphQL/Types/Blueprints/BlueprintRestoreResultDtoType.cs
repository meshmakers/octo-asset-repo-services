using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintRestoreResultDto"/>. Outcome of a
/// <c>rollbackBlueprint</c> mutation against a previously-captured backup.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintRestoreResultDtoType : ObjectGraphType<BlueprintRestoreResultDto>
{
    public BlueprintRestoreResultDtoType()
    {
        Name = "BlueprintRestoreResult";
        Description = "Result of restoring a tenant from a blueprint backup.";

        Field<NonNullGraphType<BooleanGraphType>>("success")
            .Description("True when the restore completed.")
            .Resolve(ctx => ctx.Source!.Success);

        Field<NonNullGraphType<IntGraphType>>("entitiesRestored")
            .Description("Number of entities written back into the tenant from the backup.")
            .Resolve(ctx => ctx.Source!.EntitiesRestored);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("messages")
            .Description("Diagnostic messages produced during the restore.")
            .Resolve(ctx => ctx.Source!.Messages);
    }
}
