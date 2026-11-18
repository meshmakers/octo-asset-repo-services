using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;

/// <summary>
///     Caller-initiated CK imports (<c>Models/ImportCk</c>, <c>ImportFromCatalog</c>,
///     <c>ImportFromCatalogBatch</c>) refuse every service-managed model (<c>System</c> and everything under
///     <c>System.</c>) - upgrade, same version and downgrade alike, for every caller. Service-managed models reach a tenant
///     only through the services' embedded import (recovery: <c>ClearCache</c> / <c>ReRunTenantSetup</c>), blueprint and
///     system import and tenant restore. A caller must never replace a model the services rely on (for example the
///     write-channel restriction of System.Identity) with a self-built or older version.
/// </summary>
public static class ServiceManagedImportGuard
{
    /// <summary>Stable error code of the refusal (problem details extension <c>code</c>).</summary>
    public const string Code = "CK_SERVICE_MANAGED_MODEL";

    /// <summary>Problem type of the refusal.</summary>
    public const string ProblemType = "https://schemas.meshmakers.cloud/errors/ck-service-managed-model";

    /// <summary>
    ///     True for a model name the platform owns: <c>System</c> or a name starting with <c>System.</c>, compared
    ///     case-insensitively after trimming, so that no spelling of the name gets past the refusal.
    /// </summary>
    public static bool IsServiceManaged(string? modelName)
    {
        var name = modelName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The refusal: <c>403</c> with problem details, the extensions <c>code</c> and <c>message</c> (read by the SDK as
    ///     a coded refusal) and <c>models</c> (the refused model ids). Nothing was cached, queued or imported.
    /// </summary>
    public static ObjectResult CreateResult(IReadOnlyCollection<string> refusedModelIds)
    {
        var models = refusedModelIds.Distinct(StringComparer.Ordinal).ToList();
        var message =
            $"{string.Join(", ", models)} {(models.Count == 1 ? "is a" : "are")} service-managed CK model" +
            $"{(models.Count == 1 ? "" : "s")} and cannot be imported by a caller. Nothing was imported. " +
            "Service-managed models (System and System.*) are installed by the platform services themselves; to " +
            "re-run their import for a tenant use ClearCache or ReRunTenantSetup.";
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = "Service-managed CK models cannot be imported",
            Status = StatusCodes.Status403Forbidden,
            Detail = message
        };
        problem.Extensions["code"] = Code;
        problem.Extensions["message"] = message;
        problem.Extensions["models"] = models;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentTypes = { "application/problem+json" }
        };
    }
}
