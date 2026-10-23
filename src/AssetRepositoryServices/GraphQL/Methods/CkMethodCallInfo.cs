using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#5670: what a generated CK method mutation field says about itself. Stored as field metadata under
///     <see cref="Statics.CkMethodCall" />; this is the marker by which the gateway rules recognize a method call.
/// </summary>
/// <param name="CkTypeId">The concrete CK type the field belongs to (the target type of the call).</param>
/// <param name="MethodId">Id of the method as declared (<c>Method-1</c>).</param>
/// <param name="QualifiedMethodId">Model-version-less qualified id of the method (<c>Model/Type.Method-1</c>), with the declaring element (the base type or interface for an inherited method).</param>
/// <param name="Kind">Instance (takes the target <c>rtId</c>) or static.</param>
internal sealed record CkMethodCallInfo(
    RtCkId<CkTypeId> CkTypeId,
    string MethodId,
    string QualifiedMethodId,
    CkMethodKindDto Kind);
