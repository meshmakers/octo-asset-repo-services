using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using CkTypeAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeAttributeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     AB#5191: a <see cref="CkTypeAttributeDto" /> that additionally carries the ownership of the
///     ASSIGNMENT - both the value that actually applies and the per-assignment override that produced it.
/// </summary>
/// <remarks>
///     Both are needed. <see cref="OwnershipOverride" /> answers "was this overridden on the type or record
///     that declares the assignment?"; <see cref="Ownership" /> answers "what happens to this value when the
///     blueprint is applied again?". Making a caller combine a nullable override with a nullable definition
///     value is how the wrong answer gets computed, which is the failure this work item exists to prevent.
/// </remarks>
internal sealed class OwnershipAwareCkTypeAttributeDto : CkTypeAttributeDto
{
    /// <summary>
    ///     EFFECTIVE ownership of this assignment as the construction-kit compiler resolved it: the
    ///     per-assignment override when one is declared, otherwise the attribute definition's ownership.
    ///     Taken straight from the compiled model graph, never recomputed here.
    /// </summary>
    public required AttributeOwnershipDto Ownership { get; init; }

    /// <summary>
    ///     The per-assignment override as DECLARED on the type or record that declares this assignment, or
    ///     null when the assignment inherits the attribute definition's ownership - which is what every
    ///     assignment authored before AB#5187 does.
    /// </summary>
    public AttributeOwnershipDto? OwnershipOverride { get; init; }
}
