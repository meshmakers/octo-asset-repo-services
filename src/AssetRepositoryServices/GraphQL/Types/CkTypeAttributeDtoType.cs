using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using CkTypeAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeAttributeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeAttributeDtoType : ObjectGraphType<CkTypeAttributeDto>
{
    public CkTypeAttributeDtoType()
    {
        Name = "CkTypeAttribute";
        Description = "Attributes of a construction kit type";

        Field(x => x.CkAttributeId, typeof(NonNullGraphType<CkIdGraph<CkAttributeId>>))
            .Description("Construction kit attribute id.");
        Field(x => x.AttributeName, typeof(NonNullGraphType<StringGraphType>))
            .Description("Attribute name within the entity.");
        Field(x => x.AttributeValueType, typeof(NonNullGraphType<AttributeValueTypesDtoType>))
            .Description("Value type of the attribute.");
        Field(x => x.AutoCompleteValues, typeof(ListGraphType<StringGraphType>))
            .Description("Auto complete values for the attribute.");
        Field(x => x.AutoIncrementReference, typeof(StringGraphType))
            .Description("Auto increment reference for the attribute.");
        Field(x => x.IsOptional)
            .Description("Defines if the attribute is optional.");
        Field(x => x.Attribute, typeof(CkAttributeDtoType))
            .Description("The construction kit attribute definition");
        Field<NonNullGraphType<AttributeOwnershipDtoType>>("ownership")
            .Description("Ownership that actually APPLIES to this attribute on this type or record, and " +
                         "therefore what installing the blueprint again does to the value (AB#5187): " +
                         "SEED_OWNED is overwritten by a re-apply and is exported; TENANT_OWNED keeps the " +
                         "tenant's value on a re-apply and is still exported; RUNTIME_STATE and SECRET keep the " +
                         "existing value on a re-apply and are excluded from an export. It is the per-assignment " +
                         "override when one is declared (see ownershipOverride), otherwise the attribute " +
                         "definition's ownership - this is the value to render, not a value to recompute.")
            .Resolve(ctx => ResolveOwnership(ctx));
        Field<AttributeOwnershipDtoType>("ownershipOverride")
            .Description("The per-assignment override as declared on the type or record that declares this " +
                         "assignment, or null when the assignment inherits the attribute definition's ownership " +
                         "(attribute.ownership). Null is the normal case; a non-null value means this type or " +
                         "record deliberately answers the ownership question differently from the shared " +
                         "attribute definition. Use ownership, not this field, to decide what a re-apply does.")
            .Resolve(ctx => (ctx.Source as OwnershipAwareCkTypeAttributeDto)?.OwnershipOverride);
        // Deliberately NO isRuntimeState field — see the note in CkAttributeDtoType (AB#5191).
    }

    private static AttributeOwnershipDto ResolveOwnership(IResolveFieldContext<CkTypeAttributeDto> arg)
    {
        if (arg.Source is OwnershipAwareCkTypeAttributeDto ownershipAwareCkTypeAttributeDto)
        {
            return ownershipAwareCkTypeAttributeDto.Ownership;
        }

        // Never guess here: reporting SEED_OWNED for an assignment whose ownership is unknown would tell an
        // operator that a credential is safe to overwrite. Every construction site fills the value in.
        throw new InvalidOperationException(
            $"Ownership of construction kit attribute assignment '{arg.Source.AttributeName}' was not resolved.");
    }
}