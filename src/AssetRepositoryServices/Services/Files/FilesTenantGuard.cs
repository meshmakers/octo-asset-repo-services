using System.Security.Claims;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     What a foreign-tenant user token looks like: for the log line only, never a token and never a metric label.
/// </summary>
internal sealed record FilesTenantMismatch(string Account, string ClientId, string TokenTenantId);

/// <summary>
///     The shared decision of the interim tenant guard on System.Files data (AB#6579 REST, AB#6624 GraphQL and
///     large binaries). One place, so every surface refuses exactly the same callers.
/// </summary>
/// <remarks>
///     Mirrors the user-token decision of <c>TenantAuthorizationMiddleware</c> (octo-common-services): only a
///     bearer-authenticated principal with a user subject (<c>sub</c> / NameIdentifier) is checked, and its
///     <c>tenant_id</c> claim must equal the route tenant (case-insensitive). Client-credentials / service
///     tokens (no subject; AB#5032) and cookie principals are not touched. No parent-tenant exemption.
///     Removed when the platform-wide Enforce (AB#5054) is armed.
/// </remarks>
internal static class FilesTenantGuard
{
    /// <summary>
    ///     Lenient parsing of <c>OCTO_Files__TenantGuard</c>: anything but a defined mode name means Enforce
    ///     (fail closed), with a warning, so a typo never opens the guard and never stops the service.
    /// </summary>
    public static FilesTenantGuardMode ParseMode(ILogger logger, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return FilesTenantGuardMode.Enforce;
        }

        if (Enum.TryParse<FilesTenantGuardMode>(configured.Trim(), true, out var mode) &&
            Enum.IsDefined(mode))
        {
            return mode;
        }

        logger.LogWarning(
            "Invalid Files:TenantGuard value '{Value}' (expected Enforce or LogOnly); using Enforce (AB#6579)",
            configured);
        return FilesTenantGuardMode.Enforce;
    }

    /// <summary>
    ///     Pure check, no side effects: a bearer user token whose tenant differs from the route tenant.
    /// </summary>
    public static FilesTenantMismatch? FindMismatch(ClaimsPrincipal? user, string routeTenantId)
    {
        if (user?.Identity is not { IsAuthenticated: true, AuthenticationType: { } authType } ||
            !authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            !user.HasClaim(c => c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier))
        {
            return null;
        }

        var tokenTenantId = user.FindFirstValue("tenant_id");
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new FilesTenantMismatch(
            user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
            user.FindFirstValue("client_id") ?? "<none>",
            string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId);
    }

    /// <summary>
    ///     Applies the configured mode to a mismatch: <c>true</c> = refuse (Enforce; counter and warning),
    ///     <c>false</c> = let the request pass (LogOnly; warning only).
    /// </summary>
    public static bool ShouldRefuse(ILogger logger, FilesTenantMismatch mismatch, string routeTenantId,
        FilesTenantGuardMode mode, string surface)
    {
        if (mode == FilesTenantGuardMode.LogOnly)
        {
            logger.LogWarning(
                "Files tenant_mismatch (LogOnly, request allowed) on {Surface}: user token of account '{Account}' " +
                "(client '{ClientId}') was issued for tenant '{TokenTenantId}' but addresses tenant '{TenantId}' " +
                "(AB#6579)",
                surface, mismatch.Account, mismatch.ClientId, mismatch.TokenTenantId, routeTenantId);
            return false;
        }

        FilesAccessMetrics.RecordTenantMismatch();
        logger.LogWarning(
            "Files API access denied (403) tenant_mismatch on {Surface}: user token of account '{Account}' " +
            "(client '{ClientId}') was issued for tenant '{TokenTenantId}' but addresses tenant '{TenantId}' " +
            "(AB#6579)",
            surface, mismatch.Account, mismatch.ClientId, mismatch.TokenTenantId, routeTenantId);
        return true;
    }

    /// <summary>The refusal text shared by all surfaces.</summary>
    public static string RefusalMessage(string routeTenantId) =>
        $"Access to the files of tenant '{routeTenantId}' is denied: the token was issued for another " +
        "tenant. Sign in to the tenant to access its files.";
}
