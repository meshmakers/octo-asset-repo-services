using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintUninstallDeletedEntityDto"/> (AB#8966): an entity an uninstall erased (or
/// would erase). Identity only, never values.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintUninstallDeletedEntityDtoType : ObjectGraphType<BlueprintUninstallDeletedEntityDto>
{
    public BlueprintUninstallDeletedEntityDtoType()
    {
        Name = "BlueprintUninstallDeletedEntity";
        Description = "An entity a blueprint uninstall erased (in a dry run: would erase). Identity only, never values.";

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

        Field<NonNullGraphType<BooleanGraphType>>("tenantOwned")
            .Description("True for a tenant-owned seed entity erased because the tenant never edited it; false for a locked entity.")
            .Resolve(ctx => ctx.Source!.TenantOwned);
    }
}
