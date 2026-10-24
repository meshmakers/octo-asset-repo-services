using GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7025: the refusal of the generic <c>create</c> (typed and untyped) for a CK type that has a static method with
///     <c>replacesGenericCreate: true</c> (AB#7024), declared on the type itself or inherited from a base type. ONE
///     GraphQL error whose <c>extensions</c> carry the machine-readable contract: <c>code</c> =
///     <see cref="Statics.GraphQlGenericCreateReplacedByMethod" /> and <c>parameters</c> (<c>ckTypeId</c> = the type the
///     caller tried to create, <c>methodId</c> = the model-version-less qualified method id, e.g.
///     <c>Model/Type.Create-1</c>). The message is fixed and language-neutral (ids only, no entity data), so two callers
///     get a byte-identical answer. The code lives in the GraphQL layer; it is not a code of the method response
///     envelope.
/// </summary>
internal static class GenericCreateReplacedByMethodGraphQlError
{
    /// <summary>Extension member with the error code.</summary>
    public const string CodeKey = CkMethodGraphQlError.CodeKey;

    /// <summary>Extension member with the language-neutral error parameters.</summary>
    public const string ParametersKey = CkMethodGraphQlError.ParametersKey;

    /// <summary>Parameter with the CK type the caller tried to create.</summary>
    public const string CkTypeIdParameter = "ckTypeId";

    /// <summary>Parameter with the qualified id of the method that replaces the generic create.</summary>
    public const string MethodIdParameter = "methodId";

    public static ExecutionError Create(string ckTypeId, string methodId)
    {
        return new ExecutionError(
            $"The generic create of '{ckTypeId}' is replaced by the CK method '{methodId}'. Call the method instead.")
        {
            Code = Statics.GraphQlGenericCreateReplacedByMethod,
            Extensions = new Dictionary<string, object?>
            {
                [CodeKey] = Statics.GraphQlGenericCreateReplacedByMethod,
                [ParametersKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [CkTypeIdParameter] = ckTypeId,
                    [MethodIdParameter] = methodId
                }
            }
        };
    }

    /// <summary>
    ///     The refusal for <paramref name="rtCkTypeId" />, or <c>null</c> when the generic create is allowed. The type is
    ///     read by its version-less runtime id from the tenant's CK cache; the methods of the type graph include the
    ///     inherited ones, so a derived concrete type is refused with the inherited method id. A type that is not in the
    ///     cache is not refused here (the create fails on its own).
    /// </summary>
    public static ExecutionError? TryCreate(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> rtCkTypeId)
    {
        // AB#7448: the lookup is the shared engine helper (AB#7121), the same one MCP and blueprint apply use.
        return GenericCreateReplacement.TryGetReplacingMethod(ckCacheService, tenantId, rtCkTypeId, out var replacing)
            ? Create(rtCkTypeId.SemanticVersionedFullName, replacing.QualifiedMethodId)
            : null;
    }
}
