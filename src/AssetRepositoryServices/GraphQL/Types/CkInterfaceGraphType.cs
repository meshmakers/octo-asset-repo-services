using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     CK v2 (AB#5667, contract §4.2): a GraphQL interface for a CK interface (e.g. <c>SystemIdentityNamed</c> for
///     <c>System.Identity/Named-1</c>). Unlike abstract-type interfaces (<c>&lt;Type&gt;Interface</c>) a CK interface
///     has no name suffix; compiler rule I-5 prevents collisions with type names of the same model. It carries the
///     system fields and the interface's attribute members; concrete types that implement the CK interface list it.
///     Phase 0 has no field that returns it (<c>runtime.byInterface</c> is a later phase), it is visible through
///     introspection and usable in fragments.
/// </summary>
[DoNotRegister]
internal sealed class CkInterfaceGraphType : InterfaceGraphType<RtEntityDto>
{
    public CkInterfaceGraphType(CkInterfaceGraph ckInterfaceGraph)
    {
        CkInterfaceId = ckInterfaceGraph.CkInterfaceId;

        Name = ckInterfaceGraph.CkInterfaceId.ToRtCkId().GetGraphQlPascalCaseName();
        Description = ckInterfaceGraph.Description ??
                      $"Construction kit interface '{ckInterfaceGraph.CkInterfaceId.SemanticVersionedFullName}'";

        // Same system fields as RtEntityInterfaceType / RtEntityDtoType, so every implementing object type matches.
        Field(d => d.RtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.RtCreationDateTime, typeof(UtcDateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(UtcDateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtCreatedBy, true)
            .Description("Subject id of the identity that created the entity (engine-stamped; read-only).");
        Field<NonNullGraphType<StringGraphType>>("rtDisplayName")
            .Description("Engine-computed display name (from the CK type's displayNameRule). " +
                         "Falls back to '<ckTypeId>@<rtId>' when no computed value is stored. " +
                         "Filtering and sorting operate on the stored value.");
        Field(x => x.RtDisplayDescription, true)
            .Description("Engine-computed display description (from the CK type's displayDescriptionRule).");
        Field(x => x.RtVersion, true);

        // Resolution happens through IsTypeOf of the implementing object types.
        ResolveType = _ => null;
    }

    public CkId<CkInterfaceId> CkInterfaceId { get; }

    /// <summary>
    ///     Adds one field per interface member. Must run after enums and records exist in the cache.
    /// </summary>
    public void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache,
        CkInterfaceGraph ckInterfaceGraph)
    {
        var builder = OctoBuilder<RtEntityDto>.Create(this, options);
        foreach (var attribute in ckInterfaceGraph.Attributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, isInputType: false, isInterface: true);
        }
    }
}
