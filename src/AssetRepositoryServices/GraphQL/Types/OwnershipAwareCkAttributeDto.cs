using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using CkAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkAttributeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     AB#5191: a <see cref="CkAttributeDto" /> that additionally carries the attribute DEFINITION's
///     resolved <see cref="AttributeOwnershipDto" />, so <c>CkAttribute.ownership</c> can be answered.
/// </summary>
/// <remarks>
///     The transport DTO lives in <c>Meshmakers.Octo.Communication.Contracts</c>, a NuGet contract this
///     service only consumes, and it has no ownership member. Rather than resolving the value again in the
///     field resolver - which would need a construction-kit cache lookup that throws for attributes coming
///     from a model that is not loaded for the tenant - every place that builds the DTO fills the value in
///     from the source it already holds: <see cref="AttributeOwnership.Resolve(AttributeOwnershipDto?, bool)" />
///     for a persisted attribute, the compiled model graph for a cached one. <c>required</c> makes that a
///     compile-time obligation: a new construction site cannot forget it and silently report SEED_OWNED,
///     which would tell an operator that a credential is safe to overwrite.
/// </remarks>
internal sealed class OwnershipAwareCkAttributeDto : CkAttributeDto
{
    /// <summary>
    ///     Resolved ownership of the attribute DEFINITION - the declared <c>ownership</c>, or the deprecated
    ///     <c>isRuntimeState</c> alias mapped onto it. Never null: an undeclared attribute resolves to
    ///     <see cref="AttributeOwnershipDto.SeedOwned" />.
    /// </summary>
    public required AttributeOwnershipDto Ownership { get; init; }
}
