using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// Wire-level shape of <c>BlueprintBlankingConfirmationInput</c> (AB#6315). Same members as the
/// REST <see cref="BlueprintBlankingConfirmationDto"/>.
/// </summary>
internal sealed class BlueprintBlankingConfirmationInputDto
{
    /// <summary>Runtime id of the entity (matches <c>BlueprintBlankedAttribute.rtId</c>).</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>Attribute name (matches <c>BlueprintBlankedAttribute.attributeName</c>).</summary>
    public string AttributeName { get; set; } = string.Empty;
}
