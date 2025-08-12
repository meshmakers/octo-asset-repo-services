using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Represents a runtime entity generic association in OctoMesh
/// </summary>
public class RtEntityGenericAssociation(RtEntityDto rtEntityDto)
{
    /// <summary>
    /// Gets the association role id of the association.
    /// </summary>
    public RtEntityDto RtEntityDto { get; } = rtEntityDto;
}