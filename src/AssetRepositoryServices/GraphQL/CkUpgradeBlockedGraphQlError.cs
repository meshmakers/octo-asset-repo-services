using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     AB#6935: the GraphQL form of the tenant upgrade guard refusal of <c>blueprints.install</c> and
///     <c>blueprints.applyUpdate</c> (AB#6871). ONE error whose <c>extensions</c> carry the machine-readable contract:
///     <c>code</c> = <c>CK_UPGRADE_BLOCKED</c>, <c>blueprintId</c>, <c>updateSet</c> and <c>blockers</c> (the members of
///     the library status / REST blocker, camelCase). The data is written to <see cref="ExecutionError.Extensions" />
///     explicitly: <c>ExecutionError.Data</c> is not sent to clients (GraphQL.NET <c>ExposeData</c> is off) and
///     must stay off, because exception data can carry internals.
/// </summary>
/// <remarks>
///     The English message does not name the REST / CLI override (<c>ForceBreakingUpdate</c>): GraphQL has no such
///     input. The REST and CLI messages are built by <see cref="CkUpgradeBlockedError" /> and are unchanged.
/// </remarks>
internal static class CkUpgradeBlockedGraphQlError
{
    /// <summary>Extension member with the blocker list.</summary>
    public const string BlockersKey = "blockers";

    /// <summary>Extension member with the full ids of the evaluated CK update set.</summary>
    public const string UpdateSetKey = "updateSet";

    /// <summary>Extension member with the requested blueprint id.</summary>
    public const string BlueprintIdKey = "blueprintId";

    public static ExecutionError Create(string blueprintId, IReadOnlyList<string> updateSet,
        IReadOnlyList<BlueprintCkUpgradeBlocker> blockers)
    {
        var dtos = BlueprintUpdatePreviewMapper.ToDtos(blockers);
        return new ExecutionError(CreateMessage(blueprintId, updateSet, dtos))
        {
            Code = CkUpgradeBlockedError.Code,
            Extensions = new Dictionary<string, object?>
            {
                [BlueprintIdKey] = blueprintId,
                [UpdateSetKey] = updateSet.ToList(),
                [BlockersKey] = dtos.Select(ToExtension).ToList()
            }
        };
    }

    public static string CreateMessage(string blueprintId, IReadOnlyList<string> updateSet,
        IReadOnlyList<CkUpgradeBlockerDto> blockers)
    {
        var dependents = blockers.Select(b => b.Dependent).Distinct(StringComparer.Ordinal).Count();
        return $"Blueprint {blueprintId} would install {string.Join(", ", updateSet)}, which would break " +
               $"{dependents} installed model(s): {string.Join("; ", blockers.Select(Describe))}. " +
               "Nothing was changed. Update the dependent models first, then repeat the request.";
    }

    private static Dictionary<string, object?> ToExtension(CkUpgradeBlockerDto b) => new()
    {
        ["dependent"] = b.Dependent,
        ["update"] = b.Update,
        ["kind"] = b.Kind,
        ["subject"] = b.Subject,
        ["code"] = b.Code,
        ["message"] = b.Message,
        ["dependentIsInUpdateSet"] = b.DependentIsInUpdateSet,
        ["suggestedDependentUpdate"] = b.SuggestedDependentUpdate,
        ["resolveHint"] = b.ResolveHint
    };

    private static string Describe(CkUpgradeBlockerDto blocker) =>
        string.IsNullOrEmpty(blocker.ResolveHint) ? blocker.Message : $"{blocker.Message} ({blocker.ResolveHint})";
}
