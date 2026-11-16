using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintTenantOwnedKeptEntityDto"/> (AB#8966): a tenant-owned seed entity a
/// blueprint uninstall keeps, with the reason. Identity only, never values.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintTenantOwnedKeptEntityDtoType : ObjectGraphType<BlueprintTenantOwnedKeptEntityDto>
{
    public BlueprintTenantOwnedKeptEntityDtoType()
    {
        Name = "BlueprintTenantOwnedKeptEntity";
        Description = "A tenant-owned seed entity a blueprint uninstall keeps, with the reason. Identity only, never values.";

        Field<NonNullGraphType<StringGraphType>>("key")
            .Description("Identity key of the seed entity: its well-known name, else its runtime id.")
            .Resolve(ctx => ctx.Source!.Key);

        Field<NonNullGraphType<StringGraphType>>("ckTypeId")
            .Description("Construction-kit type of the entity.")
            .Resolve(ctx => ctx.Source!.CkTypeId);

        Field<StringGraphType>("entityId")
            .Description("Runtime id of the entity.")
            .Resolve(ctx => ctx.Source!.EntityId);

        Field<StringGraphType>("wellKnownName")
            .Description("Well-known name of the entity, when it has one.")
            .Resolve(ctx => ctx.Source!.WellKnownName);

        Field<NonNullGraphType<StringGraphType>>("reason")
            .Description("Why the entity is kept: Edited (the tenant changed it), Referenced (an entity that stays shares an association with it) NotComparable (whether it was edited cannot be decided) or EraseFailed (the erase failed; see warnings).")
            .Resolve(ctx => ctx.Source!.Reason);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("referencedBy")
            .Description("For Referenced: the runtime ids of the entities that keep it. Empty otherwise.")
            .Resolve(ctx => ctx.Source!.ReferencedBy);
    }
}
