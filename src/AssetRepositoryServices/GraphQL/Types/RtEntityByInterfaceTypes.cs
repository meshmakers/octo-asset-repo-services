using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     AB#6932: the union of all concrete object types that implement a CK interface - the item type of
///     <c>runtime.byInterface</c>. It is a union (not a GraphQL interface) so that no existing object type changes:
///     the schema of a tenant gets only the new field and these two types. A client selects the interface members with
///     a fragment on the CK interface (<c>... on SystemIdentityNamed { name }</c>) and the type specific fields with a
///     fragment on the object type. <c>__typename</c> is the exact type of the entity (resolved by its ckTypeId, not by
///     the type hierarchy).
/// </summary>
[DoNotRegister]
internal sealed class RtEntityByInterfaceUnionType : UnionGraphType
{
    public const string TypeName = "RtEntityByInterface";

    public RtEntityByInterfaceUnionType(IReadOnlyList<RtEntityDtoType> implementers)
    {
        Name = TypeName;
        Description = "A runtime entity of any construction kit type that implements a CK interface. " +
                      "Select interface members with a fragment on the CK interface.";

        var byCkTypeId = new Dictionary<RtCkId<CkTypeId>, RtEntityDtoType>();
        foreach (var implementer in implementers)
        {
            AddPossibleType(implementer);
            byCkTypeId[implementer.CkTypeId.ToRtCkId()] = implementer;
        }

        ResolveType = obj => obj is RtEntityDto dto && byCkTypeId.TryGetValue(dto.CkTypeId, out var type)
            ? type
            : null;
    }
}

/// <summary>
///     The page of <c>runtime.byInterface</c> (AB#6932).
/// </summary>
internal sealed record RtEntityByInterfaceConnection(IReadOnlyList<RtEntityDto> Items, int? TotalCount);

/// <summary>
///     Connection-like result of <c>runtime.byInterface</c>: the page of entities and, only when selected,
///     <c>totalCount</c> (counted per collection by the engine, O(matching rows)).
/// </summary>
[DoNotRegister]
internal sealed class RtEntityByInterfaceConnectionType : ObjectGraphType<RtEntityByInterfaceConnection>
{
    public const string TypeName = RtEntityByInterfaceUnionType.TypeName + "Connection";

    public RtEntityByInterfaceConnectionType(RtEntityByInterfaceUnionType itemType)
    {
        Name = TypeName;
        Description = "A page of the entities that implement a CK interface (runtime.byInterface).";

        Field<IntGraphType>("totalCount")
            .Description("Number of all matching entities, ignoring skip and take. Counted only when this field is " +
                         "selected; the cost grows with the number of matching entities.")
            .Resolve(ctx => ctx.Source.TotalCount);

        Field("items", new NonNullGraphType(new ListGraphType(new NonNullGraphType(itemType))))
            .Description("The entities of the page, in the requested order.")
            .Resolve(ctx => ctx.Source.Items);
    }
}
