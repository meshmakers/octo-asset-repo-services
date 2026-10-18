using System.Security.Claims;
using Duende.IdentityModel;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;

/// <summary>
///     Names the caller of a request for the audit line of a forced breaking CK update (AB#6870 / AB#6871): subject,
///     client id and name from the token, never the token itself.
/// </summary>
internal static class CallerDescription
{
    public static string From(ClaimsPrincipal user)
    {
        var subject = user.FindFirst(JwtClaimTypes.Subject)?.Value;
        var clientId = user.FindFirst(JwtClaimTypes.ClientId)?.Value;
        var name = user.FindFirst(JwtClaimTypes.Name)?.Value ?? user.Identity?.Name;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(subject)) parts.Add($"sub={subject}");
        if (!string.IsNullOrEmpty(clientId)) parts.Add($"client_id={clientId}");
        if (!string.IsNullOrEmpty(name)) parts.Add($"name={name}");
        return parts.Count > 0 ? string.Join(" ", parts) : "unknown caller";
    }
}
