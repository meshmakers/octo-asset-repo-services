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
    /// <summary>
    ///     Tenant administration (AB#4497): create, attach, detach, clear, delete, rerunSetup of child tenants.
    ///     Full-access scope plus the tenant role <c>TenantManagement</c>.
    /// </summary>
    internal const string TenantAdministrationPolicy = "TenantAdministrationPolicy";
    /// <summary>
    ///     Changing the tenant's data model (AB#8529): ImportCk, ImportFromCatalog(Batch) / FixAll, blueprint
    ///     install / update / uninstall, CK model and blueprint catalog refresh. Full-access scope plus the tenant role
    ///     <c>DataModelManagement</c>; a refusal is a <c>403</c> with code <see cref="DataModelManagementRequiredCode" />.
    /// </summary>
    internal const string DataModelManagementPolicy = "DataModelManagementPolicy";

    /// <summary>
    ///     AB#8529: stable error code of a refused <see cref="DataModelManagementPolicy" /> (REST <c>403</c> body
    ///     <c>code</c>, GraphQL error <c>extensions.code</c>).
    /// </summary>
    internal const string DataModelManagementRequiredCode = "DataModelManagementRequired";

    /// <summary>
    ///     AB#8559: stable error code of a stream-data read refused because the caller holds none of the tenant roles
    ///     <c>StreamDataReader</c>, <c>StreamDataWriter</c> or <c>StreamDataAdmin</c> (GraphQL error <c>extensions.code</c>,
    ///     only with <c>StreamDataReadAuthorization:RoleEnforcement=Enforce</c>).
    /// </summary>
    internal const string StreamDataReadRoleRequiredCode = "StreamDataReadRoleRequired";


    /// <summary>
    /// Policy used for Graph API to allow authenticated users
    /// </summary>
    internal const string AuthenticatedUserPolicyGraphApi = "AuthenticatedUserPolicyGraphAPI";

    /// <summary>
    /// Policy used to allow authenticated users with redirect to the login page for Playground
    /// </summary>
    internal const string AuthenticatedUserPolicy = "AuthenticatedUserPolicy";

    /// <summary>
    /// Name of the key for identity data version
    /// </summary>
    public const string AssetServiceIdentityDataVersionKey = "AssetServicesIdentityData";

    /// <summary>
    /// Expected version of identity data
    /// </summary>
    public const int AssetServiceIdentityDataVersionValue = 5;


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