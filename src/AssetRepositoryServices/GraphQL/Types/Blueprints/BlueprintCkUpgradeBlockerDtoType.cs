using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="CkUpgradeBlockerDto"/> for blueprint updates (tenant upgrade guard, AB#6871):
/// one reason why an installed model would break if the CK models of a blueprint were installed.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintCkUpgradeBlockerDtoType : ObjectGraphType<CkUpgradeBlockerDto>
{
    public BlueprintCkUpgradeBlockerDtoType()
    {
        Name = "BlueprintCkUpgradeBlocker";
        Description = "An installed CK model that would break if the CK models of a blueprint update were installed (tenant upgrade guard).";

        Field<NonNullGraphType<StringGraphType>>("dependent")
            .Description("The model that would break, full id.")
            .Resolve(ctx => ctx.Source!.Dependent);

        Field<NonNullGraphType<StringGraphType>>("update")
            .Description("The model of the update that causes it, full id.")
            .Resolve(ctx => ctx.Source!.Update);

        Field<NonNullGraphType<StringGraphType>>("kind")
            .Description("Blocker kind (ExactPin, FloorNotMet, RangeNotSatisfied, ElementRemoved, ElementChanged, ElementBecameInternal, NameCollision). Clients must tolerate new kinds.")
            .Resolve(ctx => ctx.Source!.Kind);

        Field<NonNullGraphType<StringGraphType>>("subject")
            .Description("The pin, range or element / member the blocker is about.")
            .Resolve(ctx => ctx.Source!.Subject);

        Field<NonNullGraphType<StringGraphType>>("code")
            .Description("Stable code per kind, for example CK_UPGRADE_EXACT_PIN.")
            .Resolve(ctx => ctx.Source!.Code);

        Field<NonNullGraphType<StringGraphType>>("message")
            .Description("English explanation naming the dependent.")
            .Resolve(ctx => ctx.Source!.Message);

        Field<NonNullGraphType<BooleanGraphType>>("dependentIsInUpdateSet")
            .Description("True when the dependent is itself a model of the evaluated update set.")
            .Resolve(ctx => ctx.Source!.DependentIsInUpdateSet);

        Field<StringGraphType>("suggestedDependentUpdate")
            .Description("A newer version of the dependent proven to resolve the blocker; null when there is none.")
            .Resolve(ctx => ctx.Source!.SuggestedDependentUpdate);

        Field<StringGraphType>("resolveHint")
            .Description("Human-readable next step when suggestedDependentUpdate is set; null otherwise.")
            .Resolve(ctx => ctx.Source!.ResolveHint);
    }
}
