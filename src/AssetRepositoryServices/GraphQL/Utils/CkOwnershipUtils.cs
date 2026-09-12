using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#5191 helper: recovers the per-assignment ownership overrides that a compiled type or record graph
///     no longer keeps separately.
/// </summary>
internal static class CkOwnershipUtils
{
    /// <summary>
    ///     Collects the DECLARED per-assignment ownership overrides of the given declaring scopes, keyed by
    ///     attribute id. Only declared (non-null) overrides are returned - a missing key means "this
    ///     assignment inherits the attribute definition's ownership".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>CkTypeAttributeGraph.Ownership</c> is already resolved against the definition, so the raw
    ///         override has to be read from the declaring scope's <c>DefinedAttributes</c>, which are the
    ///         untouched declarations. Pass the queried type or record first and its base types after it:
    ///         the attributes connection returns inherited assignments too, and an inherited assignment is
    ///         declared - and possibly overridden - on the base type, not on the queried one.
    ///     </para>
    ///     <para>
    ///         An attribute id may be declared at most once along an inheritance chain (a redeclaration is
    ///         rejected at compile time as CkTypeIdAttributeIdNotUniqueByInheritance), so the first hit is
    ///         the only hit and the scope order matters only for robustness, not for precedence.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyDictionary<CkId<CkAttributeId>, AttributeOwnershipDto> CollectDeclaredOwnershipOverrides(
        IEnumerable<CkTypeWithAttributesGraph> declaringScopes)
    {
        var declaredOverrides = new Dictionary<CkId<CkAttributeId>, AttributeOwnershipDto>();

        foreach (var declaringScope in declaringScopes)
        {
            foreach (var definedAttribute in declaringScope.DefinedAttributes)
            {
                if (definedAttribute.Ownership.HasValue)
                {
                    declaredOverrides.TryAdd(definedAttribute.CkAttributeId, definedAttribute.Ownership.Value);
                }
            }
        }

        return declaredOverrides;
    }

    /// <summary>
    ///     Returns the declared per-assignment override for the given attribute, or null when the assignment
    ///     inherits the attribute definition's ownership.
    /// </summary>
    internal static AttributeOwnershipDto? GetDeclaredOwnershipOverride(
        IReadOnlyDictionary<CkId<CkAttributeId>, AttributeOwnershipDto> declaredOverrides,
        CkId<CkAttributeId> ckAttributeId)
    {
        return declaredOverrides.TryGetValue(ckAttributeId, out var declaredOverride) ? declaredOverride : null;
    }
}
