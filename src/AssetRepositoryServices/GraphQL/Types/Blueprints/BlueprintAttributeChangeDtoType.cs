using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintAttributeChangeDto"/>: one attribute a blueprint
/// update would change, old and new value rendered as text.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintAttributeChangeDtoType : ObjectGraphType<BlueprintAttributeChangeDto>
{
    public BlueprintAttributeChangeDtoType()
    {
        Name = "BlueprintAttributeChange";
        Description = "One attribute a blueprint update would change: the tenant's value and the seed's value, as text (scalars verbatim, records and lists as JSON).";

        Field<NonNullGraphType<StringGraphType>>("attributeName")
            .Description("Attribute name as declared on the CK type.")
            .Resolve(ctx => ctx.Source!.AttributeName);

        Field<StringGraphType>("oldValue")
            .Description("The tenant's current value; null when the tenant has no value.")
            .Resolve(ctx => ctx.Source!.OldValue);

        Field<StringGraphType>("newValue")
            .Description("The seed's value the update would write; null when the seed clears the attribute.")
            .Resolve(ctx => ctx.Source!.NewValue);
    }
}
