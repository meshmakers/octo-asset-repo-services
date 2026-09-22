using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintEntityChangeDto"/>: one entity the update would
/// change, with its attribute-level diff (AB#5297, AB#5308).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintEntityChangeDtoType : ObjectGraphType<BlueprintEntityChangeDto>
{
    public BlueprintEntityChangeDtoType()
    {
        Name = "BlueprintEntityChange";
        Description = "An entity a blueprint update would change, with the attributes that differ between tenant and seed.";

        Field<NonNullGraphType<StringGraphType>>("entityId")
            .Description("Runtime id of the entity.")
            .Resolve(ctx => ctx.Source!.EntityId);

        Field<StringGraphType>("entityWellKnownName")
            .Description("Well-known name of the entity, when the seed assigns one.")
            .Resolve(ctx => ctx.Source!.EntityWellKnownName);

        Field<StringGraphType>("entityDisplayName")
            .Description("Display name of the entity on the tenant, when it has one.")
            .Resolve(ctx => ctx.Source!.EntityDisplayName);

        Field<NonNullGraphType<StringGraphType>>("entityCkTypeId")
            .Description("Construction-kit type of the entity.")
            .Resolve(ctx => ctx.Source!.EntityCkTypeId);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintAttributeChangeDtoType>>>>("attributes")
            .Description("The attributes that differ. Empty when `note` explains why the entity is counted without a list.")
            .Resolve(ctx => ctx.Source!.Attributes);

        Field<StringGraphType>("note")
            .Description("Set when the engine could not compare the entity attribute by attribute and counts it as changed to stay safe.")
            .Resolve(ctx => ctx.Source!.Note);
    }
}
