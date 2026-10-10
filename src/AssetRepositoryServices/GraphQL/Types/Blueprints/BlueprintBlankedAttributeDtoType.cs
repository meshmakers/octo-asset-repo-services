using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintBlankedAttributeDto"/> (AB#6315): an attribute whose
/// non-empty tenant value a blueprint update would blank. Descriptions only, never values.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintBlankedAttributeDtoType : ObjectGraphType<BlueprintBlankedAttributeDto>
{
    public BlueprintBlankedAttributeDtoType()
    {
        Name = "BlueprintBlankedAttribute";
        Description = "An attribute whose non-empty tenant value a blueprint update would blank. Values are described by kind and size, never included.";

        Field<NonNullGraphType<StringGraphType>>("rtId")
            .Description("Runtime id of the entity.")
            .Resolve(ctx => ctx.Source!.RtId);

        Field<NonNullGraphType<StringGraphType>>("ckTypeId")
            .Description("Construction-kit type of the entity.")
            .Resolve(ctx => ctx.Source!.CkTypeId);

        Field<NonNullGraphType<StringGraphType>>("attributeName")
            .Description("Attribute name as stored.")
            .Resolve(ctx => ctx.Source!.AttributeName);

        Field<NonNullGraphType<StringGraphType>>("reason")
            .Description("SeedEmpty (empty value, or a JSON text emptying a string the tenant filled), SeedOmitted (attribute not declared by the seed) or ResetToDefault (current value differs from the CK default; incoming = default).")
            .Resolve(ctx => ctx.Source!.Reason);

        Field<StringGraphType>("currentSummary")
            .Description("What the tenant holds, e.g. \"string (223 chars)\".")
            .Resolve(ctx => ctx.Source!.CurrentSummary);

        Field<StringGraphType>("incomingSummary")
            .Description("What the seed carries, e.g. \"string (110 chars)\", \"empty string\" or \"omitted\".")
            .Resolve(ctx => ctx.Source!.IncomingSummary);

        Field<NonNullGraphType<BooleanGraphType>>("appliedOnUpdate")
            .Description("False: the update keeps the tenant value. True: the update blanked it (apply result, explicit confirmation only). Always false in a preview.")
            .Resolve(ctx => ctx.Source!.AppliedOnUpdate);
    }
}
