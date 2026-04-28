namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Response payload of <see cref="StreamDataController.Status(string)"/>. Tells the studio which
/// of the three activation tiers (concept §5) is currently set: the instance-wide kill switch and
/// the per-tenant flag. The third tier (per-archive status) is observed via the regular CkArchive
/// entity reads.
/// </summary>
public sealed class StreamDataStatusDto
{
    /// <summary>
    /// True when <c>StreamData:Enabled</c> is <c>true</c> in the deployment's appsettings. When
    /// false the studio hides the StreamData navigation entirely.
    /// </summary>
    public required bool InstanceEnabled { get; init; }

    /// <summary>
    /// True when the requested tenant has opted into StreamData via
    /// <see cref="StreamDataController.Enable(string)"/>. Always false when
    /// <see cref="InstanceEnabled"/> is false.
    /// </summary>
    public required bool TenantEnabled { get; init; }
}
