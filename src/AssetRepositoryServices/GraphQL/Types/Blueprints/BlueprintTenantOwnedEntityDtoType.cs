using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintTenantOwnedEntityDto"/> (AB#6454): a tenant-owned seed
/// entity a blueprint update did not write. Identity only, never attribute values.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintTenantOwnedEntityDtoType : ObjectGraphType<BlueprintTenantOwnedEntityDto>
{
    public BlueprintTenantOwnedEntityDtoType()
    {
        Name = "BlueprintTenantOwnedEntity";
        Description = "A tenant-owned seed entity a blueprint update did not write: the tenant still holds it (skipped) or deleted it (stays deleted). Identity only, never values.";

        Field<NonNullGraphType<StringGraphType>>("key")
            .Description("Identity key of the seed entity: its well-known name, else its runtime id.")
            .Resolve(ctx => ctx.Source!.Key);

        Field<NonNullGraphType<StringGraphType>>("ckTypeId")
            .Description("Construction-kit type of the entity.")
            .Resolve(ctx => ctx.Source!.CkTypeId);

        Field<StringGraphType>("entityId")
            .Description("Runtime id on the tenant; null for an entity the tenant deleted.")
            .Resolve(ctx => ctx.Source!.EntityId);

        Field<StringGraphType>("wellKnownName")
            .Description("Well-known name of the entity, when the seed assigns one.")
            .Resolve(ctx => ctx.Source!.WellKnownName);
    }
}
