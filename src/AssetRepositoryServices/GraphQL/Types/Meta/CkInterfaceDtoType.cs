using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Meta;

/// <summary>
///     <c>type CkInterface</c> — CK v2 meta introspection of a construction kit interface (F1.5-S3, AB#5922).
/// </summary>
internal sealed class CkInterfaceDtoType : ObjectGraphType<CkInterfaceGraph>
{
    public CkInterfaceDtoType()
    {
        Name = "CkInterface";
        Description = "A construction kit interface (CK v2): a versioned contract of attribute, association and " +
                      "method members that types implement.";

        Field<NonNullGraphType<CkIdGraph<CkInterfaceId>>>("ckInterfaceId").Resolve(ctx => ctx.Source.CkInterfaceId);
        Field<NonNullGraphType<RtCkIdGraph<CkInterfaceId>>>("rtCkInterfaceId")
            .Resolve(ctx => ctx.Source.CkInterfaceId.ToRtCkId());
        Field<StringGraphType>("description").Resolve(ctx => ctx.Source.Description);
        Field<NonNullGraphType<StringGraphType>>("visibility")
            .Description("Public (default) or Internal (not referencable from other models).")
            .Resolve(ctx => ctx.Source.Visibility.ToString());
        Field<NonNullGraphType<BooleanGraphType>>("deprecated")
            .Description("Dependents get a compile warning; removed only in the next model major.")
            .Resolve(ctx => ctx.Source.Deprecated);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkIdGraph<CkInterfaceId>>>>>("extends")
            .Description("The interfaces this interface declares to extend.")
            .Resolve(ctx => ctx.Source.DeclaredExtends);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkIdGraph<CkInterfaceId>>>>>("allExtends")
            .Description("All extended interfaces (transitive).")
            .Resolve(ctx => ctx.Source.AllExtendedInterfaces);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkInterfaceAttributeDtoType>>>>("attributes")
            .Description("Attribute members, including those of extended interfaces.")
            .Resolve(ctx => ctx.Source.AllAttributes.Values);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkInterfaceAssociationDtoType>>>>("associations")
            .Description("Association members, including those of extended interfaces.")
            .Resolve(ctx => ctx.Source.AllAssociations);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodDtoType>>>>("methods")
            .Description("Method members (definitions), including those of extended interfaces.")
            .Resolve(ctx => ctx.Source.AllMethods.Values.OrderBy(m => m.Definition.MethodId, StringComparer.Ordinal)
                .Select(CkMethodDefinitionView.From));
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkIdGraph<CkTypeId>>>>>("implementingTypes")
            .Description("Types (abstract and concrete) that implement the interface directly or through a base " +
                         "type or a derived interface.")
            .Resolve(ctx => ctx.Source.ImplementingTypes);
    }
}

/// <summary>
///     An attribute member of a CK interface.
/// </summary>
internal sealed class CkInterfaceAttributeDtoType : ObjectGraphType<CkTypeAttributeGraph>
{
    public CkInterfaceAttributeDtoType()
    {
        Name = "CkInterfaceAttribute";
        Description = "Attribute member of a construction kit interface.";

        Field<NonNullGraphType<CkIdGraph<CkAttributeId>>>("ckAttributeId").Resolve(ctx => ctx.Source.CkAttributeId);
        Field<NonNullGraphType<StringGraphType>>("attributeName")
            .Description("Member name; implementing types assign the attribute under this name.")
            .Resolve(ctx => Meshmakers.Common.Shared.StringExtensions.ToCamelCase(ctx.Source.AttributeName));
        Field<NonNullGraphType<AttributeValueTypesDtoType>>("attributeValueType").Resolve(ctx => ctx.Source.ValueType);
        Field<NonNullGraphType<BooleanGraphType>>("isOptional")
            .Description("Optional members need not be assigned by implementing types.")
            .Resolve(ctx => ctx.Source.IsOptional);
    }
}

/// <summary>
///     An association member of a CK interface.
/// </summary>
internal sealed class CkInterfaceAssociationDtoType : ObjectGraphType<CkInterfaceAssociationGraph>
{
    public CkInterfaceAssociationDtoType()
    {
        Name = "CkInterfaceAssociation";
        Description = "Association member of a construction kit interface.";

        Field<NonNullGraphType<CkIdGraph<CkAssociationRoleId>>>("ckAssociationRoleId")
            .Resolve(ctx => ctx.Source.Definition.CkRoleId);
        Field<CkIdGraph<CkTypeId>>("targetCkTypeId").Resolve(ctx => ctx.Source.Definition.TargetCkTypeId);
        Field<CkIdGraph<CkInterfaceId>>("targetCkInterfaceId")
            .Resolve(ctx => ctx.Source.Definition.TargetCkInterfaceId);
        Field<StringGraphType>("multiplicity")
            .Description("Declared multiplicity (One, ZeroOrOne, N), null when not declared.")
            .Resolve(ctx => ctx.Source.Definition.Multiplicity?.ToString());
        Field<NonNullGraphType<BooleanGraphType>>("isOptional").Resolve(ctx => ctx.Source.Definition.IsOptional);
        Field<NonNullGraphType<CkIdGraph<CkInterfaceId>>>("declaringCkInterfaceId")
            .Resolve(ctx => ctx.Source.DeclaringCkInterfaceId);
    }
}
