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
}
