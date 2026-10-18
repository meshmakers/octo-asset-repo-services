namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Request to apply a blueprint update
/// </summary>
public class BlueprintUpdateRequestDto
{
    /// <summary>
    ///     Target blueprint version to update to
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Update mode: Safe, Merge, Full, Migration
    /// </summary>
    public string UpdateMode { get; set; } = "Merge";

    /// <summary>
    ///     Whether this is a dry run (preview only, no changes)
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    ///     Conflict resolutions for specific entities
    /// </summary>
    public Dictionary<string, string>? ConflictResolutions { get; set; }

    /// <summary>
    ///     Explicit confirmation that the update may blank EVERY attribute listed in the preview's
    ///     <c>BlankedAttributes</c> (AB#6315). Default <c>false</c>: tenant values are kept and the
    ///     response lists them. Prefer <see cref="ConfirmedBlankings" /> to confirm single attributes.
    /// </summary>
    public bool AllowBlanking { get; set; }

    /// <summary>
    ///     Confirms blanking for exactly these entity/attribute pairs (AB#6315); everything else the
    ///     preview lists stays kept. Ignored when <see cref="AllowBlanking" /> is true.
    /// </summary>
    public List<BlueprintBlankingConfirmationDto>? ConfirmedBlankings { get; set; }

    /// <summary>
    ///     Applies the update although the CK models it installs or raises would break an installed model (tenant
    ///     upgrade guard, CK v2 F2.5, AB#6871). Default <c>false</c>: the apply is refused with
    ///     <c>409 CK_UPGRADE_BLOCKED</c> before the first change. Requires the data model management policy (403
    ///     otherwise). Ignored by the preview and by a dry run, which only report the blockers.
    /// </summary>
    public bool ForceBreakingUpdate { get; set; }
}
