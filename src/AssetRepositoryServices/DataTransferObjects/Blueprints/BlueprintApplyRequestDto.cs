namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Request to apply a blueprint to a tenant for the first time.
/// </summary>
public class BlueprintApplyRequestDto
{
    /// <summary>
    ///     Fully-qualified blueprint id, e.g. <c>MyBlueprint-1.0.0</c>.
    /// </summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>
    ///     If true, re-apply seed data via upsert even if the same version is already
    ///     recorded for the tenant. Use this for recovery after storage corruption
    ///     or manual cleanup. Default: <c>false</c>.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    ///     Applies the blueprint although the CK models it installs or raises would break an installed model (tenant
    ///     upgrade guard, CK v2 F2.5, AB#6871). Default <c>false</c>: the apply is refused with
    ///     <c>409 CK_UPGRADE_BLOCKED</c> before the first change. Requires the data model management policy (403
    ///     otherwise). Not to be confused with <see cref="Force" />, which only re-applies seed data.
    /// </summary>
    public bool ForceBreakingUpdate { get; set; }
}
