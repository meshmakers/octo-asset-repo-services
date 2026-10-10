using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// Server-side shape of the <c>BlueprintUpdateRequestInput</c> GraphQL input. The engine
/// enums (<see cref="BlueprintUpdateMode"/>, <see cref="ConflictResolution"/>) are deserialised
/// directly; <c>ConflictResolutions</c> comes in as a list and the resolver folds it into
/// the engine's dictionary keyed by <c>EntityId</c>.
/// </summary>
internal sealed class BlueprintUpdateRequestInputDto
{
    /// <summary>Fully-qualified target version (e.g. <c>MyBlueprint-2.0.0</c>).</summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>Update reconciliation mode. Defaults to <c>Merge</c>.</summary>
    public BlueprintUpdateMode UpdateMode { get; set; } = BlueprintUpdateMode.Merge;

    /// <summary>Compute the diff without persisting changes.</summary>
    public bool DryRun { get; set; }

    /// <summary>Per-entity conflict-resolution overrides.</summary>
    public List<BlueprintConflictResolutionInputDto>? ConflictResolutions { get; set; }

    /// <summary>
    /// AB#6315: confirms blanking of EVERY attribute listed by <c>previewUpdate.blankedAttributes</c>.
    /// Default false: tenant values are kept and the result lists them.
    /// </summary>
    public bool AllowBlanking { get; set; }

    /// <summary>AB#6315: confirms blanking for exactly these entity/attribute pairs.</summary>
    public List<BlueprintBlankingConfirmationInputDto>? ConfirmedBlankings { get; set; }
}
