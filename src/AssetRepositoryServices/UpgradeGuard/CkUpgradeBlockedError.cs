using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;

/// <summary>
///     Stable error contract of the tenant upgrade guard enforcement (CK v2 F2.5, AB#6870): ImportFromCatalog and
///     ImportFromCatalogBatch refuse an update set that would break an installed model with HTTP
///     <c>409 Conflict</c> and RFC 9457 problem details (<c>application/problem+json</c>), like the blueprint-lock
///     contract (AB#6385). Language-neutral: <c>code</c>, <c>updateSet</c> and the structured <c>blockers</c>
///     (<see cref="CkUpgradeBlockerDto" />: dependent, update, kind, subject, code, resolve hint); <c>detail</c> is an
///     English sentence for clients that do not localize.
/// </summary>
public static class CkUpgradeBlockedError
{
    /// <summary>The stable error code in the <c>code</c> member of the problem details.</summary>
    public const string Code = "CK_UPGRADE_BLOCKED";

    /// <summary>The problem <c>type</c> URI; stable, not dereferenced.</summary>
    public const string ProblemType = "https://schemas.meshmakers.cloud/errors/ck-upgrade-blocked";

    /// <summary>
    ///     Builds the problem details of a refused update set: <c>code</c>, <c>updateSet</c> (full model ids, as
    ///     requested) and <c>blockers</c> (one per installed model and pin the set would break).
    /// </summary>
    public static ProblemDetails CreateProblem(IReadOnlyList<string> updateSet,
        IReadOnlyList<CkUpgradeBlockerDto> blockers)
    {
        var dependents = blockers.Select(b => b.Dependent).Distinct(StringComparer.Ordinal).ToList();
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = "The update would break installed CK models",
            Status = StatusCodes.Status409Conflict,
            Detail = $"Importing {string.Join(", ", updateSet)} would break {dependents.Count} installed model(s): " +
                     $"{string.Join("; ", blockers.Select(Describe))}. Nothing was imported. " +
                     "Update the dependent models first, or repeat the request with ForceBreakingUpdate = true " +
                     "(octo-cli: --force) to import anyway."
        };
        problem.Extensions["code"] = Code;
        problem.Extensions["updateSet"] = updateSet;
        problem.Extensions["blockers"] = blockers;
        return problem;
    }

    /// <summary>
    ///     The 409 action result with <c>application/problem+json</c>.
    /// </summary>
    public static ObjectResult CreateResult(IReadOnlyList<string> updateSet, IReadOnlyList<CkUpgradeBlockerDto> blockers) =>
        new(CreateProblem(updateSet, blockers))
        {
            StatusCode = StatusCodes.Status409Conflict,
            ContentTypes = { "application/problem+json" }
        };

    private static string Describe(CkUpgradeBlockerDto blocker) =>
        string.IsNullOrEmpty(blocker.ResolveHint) ? blocker.Message : $"{blocker.Message} ({blocker.ResolveHint})";
}
