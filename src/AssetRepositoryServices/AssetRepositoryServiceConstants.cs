namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

/// <summary>
///     Common constants for core services
/// </summary>
public static class AssetRepositoryServiceConstants
{
    internal const string SystemApiReadOnlyPolicy = "SystemApiReadOnlyPolicy";
    internal const string SystemApiReadWritePolicy = "SystemApiReadWritePolicy";
    internal const string AuthenticatedUserPolicy = "AuthenticatedUserPolicy";
    internal const string TenantApiReadWritePolicy = "TenantApiReadWritePolicy";

    /// <summary>
    /// Name of the key for identity data version
    /// </summary>
    public const string AssetServiceIdentityDataVersionKey = "AssetServicesIdentityData";

    /// <summary>
    /// Expected version of identity data
    /// </summary>
    public const int AssetServiceIdentityDataVersionValue = 1;

    /// <summary>
    ///     The name of the cookie of cookie-based auth
    /// </summary>
    public const string CookieName = "Octo-AssetRepositoryServices";

    /// <summary>
    ///     Timespan a cookie is expiring
    /// </summary>
    public static readonly TimeSpan CookieExpireTimeSpan = TimeSpan.FromMinutes(60);
}