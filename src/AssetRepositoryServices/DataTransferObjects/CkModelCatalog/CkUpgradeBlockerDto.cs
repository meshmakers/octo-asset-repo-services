namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     One reason why an installed model would break if an offered CK model update were imported (tenant upgrade
///     guard, AB#6867 / AB#6868). Language-neutral data (<see cref="Code" />, <see cref="Dependent" />,
///     <see cref="Update" />, <see cref="Subject" />) plus an English <see cref="Message" /> for clients that do not
///     localize. Read-only information: nothing is enforced by the status endpoints.
/// </summary>
public class CkUpgradeBlockerDto
{
    /// <summary>
    ///     The model that would break, full id (for example <c>EnergyCommunity.Registration-2.8.0</c>).
    /// </summary>
    public string Dependent { get; set; } = string.Empty;

    /// <summary>
    ///     The model of the update that causes it, full id (for example <c>Basic-2.5.0</c>).
    /// </summary>
    public string Update { get; set; } = string.Empty;

    /// <summary>
    ///     Blocker kind: <c>ExactPin</c>, <c>FloorNotMet</c>, <c>RangeNotSatisfied</c>, <c>ElementRemoved</c>,
    ///     <c>ElementChanged</c>, <c>ElementBecameInternal</c> or <c>NameCollision</c>. Clients must tolerate new kinds.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     The pin (<c>Basic-2.4.0</c>), range or element / member (<c>Basic@2/Asset-1.Name</c>) the blocker is about.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    ///     Stable code per kind, for example <c>CK_UPGRADE_EXACT_PIN</c>.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    ///     English explanation naming the dependent, for example
    ///     <c>EnergyCommunity.Registration 2.8.0 would break: exact pin Basic-2.4.0: Basic-2.5.0 replaces it</c>.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     True when <see cref="Dependent" /> is itself a model of the evaluated update set.
    /// </summary>
    public bool DependentIsInUpdateSet { get; set; }

    /// <summary>
    ///     A newer catalog version of the dependent (for example <c>EnergyCommunity.Registration-2.10.0</c>) that was
    ///     <b>proven</b> to resolve this blocker: the combined update set (the offered model plus this version) was
    ///     evaluated against the tenant and no blocker for the dependent remained. <c>null</c> when the catalogs offer
    ///     no newer version or the proof failed.
    /// </summary>
    public string? SuggestedDependentUpdate { get; set; }

    /// <summary>
    ///     Human-readable next step when <see cref="SuggestedDependentUpdate" /> is set, for example
    ///     <c>update EnergyCommunity.Registration to 2.10.0 first</c>; <c>null</c> otherwise.
    /// </summary>
    public string? ResolveHint { get; set; }
}
