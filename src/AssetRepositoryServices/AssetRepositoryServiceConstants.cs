namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

/// <summary>
///     Common constants for core services
/// </summary>
public static class AssetRepositoryServiceConstants
{
    private const string TenantId = "tenantId";

    internal const string SystemAssetApiReadOnlyPolicy = "SystemAssetApiReadOnlyPolicy";
    internal const string SystemAssetApiReadWritePolicy = "SystemAssetApiReadWritePolicy";

    internal const string TenantAssetApiReadOnlyPolicy = "TenantAssetApiReadOnlyPolicy";
    internal const string TenantAssetApiReadWritePolicy = "TenantAssetApiReadWritePolicy";


    internal const string AuthenticatedUserPolicy = "AuthenticatedUserPolicy";

    /// <summary>
    /// Name of the key for identity data version
    /// </summary>
    public const string AssetServiceIdentityDataVersionKey = "AssetServicesIdentityData";

    /// <summary>
    /// Expected version of identity data
    /// </summary>
    public const int AssetServiceIdentityDataVersionValue = 2;

    /// <summary>
    ///     The name of the cookie of cookie-based auth
    /// </summary>
    public const string CookieName = "Octo-AssetRepositoryServices";

    /// <summary>
    ///     Timespan a cookie is expiring
    /// </summary>
    public static readonly TimeSpan CookieExpireTimeSpan = TimeSpan.FromMinutes(60);


    internal static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }
}