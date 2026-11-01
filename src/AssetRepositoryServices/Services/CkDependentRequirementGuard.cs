using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using CkCompiledModelRoot = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot;
using CkModelDependencyDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkModelDependencyDto;
using ModelState = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.ModelState;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     AB#7998: the version requirements of the installed dependents, checked against every version an update set
///     would install. The engine's tenant upgrade guard (AB#6867) treats a candidate of <b>another major</b> than the
///     installed version as a parallel install and does not judge it, but the import replaces the installed version:
///     FixAll upgraded <c>Basic.Energy</c> 1.10.0 to 2.0.0 and <c>EnergyIQ</c> (range <c>Basic.Energy-[1.9,2.0)</c>)
///     became <c>ResolveFailed</c>. This check closes that gap for every version: a candidate must lie inside the
///     intersection of the ranges and exact pins of all dependents that stay installed (and of the models of the set).
///     <list type="bullet">
///         <item>range-retaining dependent: the declared range of its direct dependency, at or above the floor; a
///             dependency without a range entry is a transitive pin (AB#7940) and is judged by the intermediate;</item>
///         <item>classic dependent: the exact compile-time pin;</item>
///         <item>only installed <c>Available</c> dependents and only candidates whose name is installed
///             <c>Available</c> in another version; service-managed candidates only for explicit callers.</item>
///     </list>
///     Pure and read-only: no catalog, no tenant access.
/// </summary>
internal static class CkDependentRequirementGuard
{
    /// <summary>Blocker kind of a range that does not admit the candidate.</summary>
    internal const string RangeKind = "RangeNotSatisfied";

    /// <summary>Blocker kind of an exact pin on another version.</summary>
    internal const string ExactPinKind = "ExactPin";

    internal const string RangeCode = "CK_UPGRADE_RANGE_NOT_SATISFIED";
    internal const string ExactPinCode = "CK_UPGRADE_EXACT_PIN";

    /// <summary>
    ///     True for the blocker kinds that mean "a dependent does not accept this version" (a lower version may still
    ///     be accepted), as opposed to changed or removed elements.
    /// </summary>
    internal static bool IsRequirementBlocker(CkUpgradeBlockerDto blocker) =>
        blocker.Kind is RangeKind or ExactPinKind or "FloorNotMet";

    /// <summary>
    ///     The blockers of <paramref name="updateSet" />: every (dependent, candidate) whose requirement does not admit
    ///     the candidate version. Dependents are the installed <c>Available</c> models the set does not replace plus the
    ///     models of the set (judged by their new requirements).
    /// </summary>
    internal static List<CkUpgradeBlockerDto> Evaluate(IReadOnlyCollection<CkModel> installed,
        IReadOnlyCollection<CkCompiledModelRoot> updateSet, bool includeServiceManaged)
    {
        var setNames = updateSet.Select(c => c.ModelId.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependents = installed
            .Where(m => m.ModelState == ModelState.Available && !setNames.Contains(m.Id.Name))
            .Select(FromInstalled)
            .Concat(updateSet.Select(FromCompiled))
            .ToList();

        var blockers = new List<CkUpgradeBlockerDto>();
        foreach (var candidate in updateSet.Select(c => c.ModelId))
        {
            if (!IsReplacement(installed, candidate, includeServiceManaged))
            {
                continue;
            }

            blockers.AddRange(dependents.Select(d => Judge(d, candidate)).OfType<CkUpgradeBlockerDto>());
        }

        return blockers;
    }

    /// <summary>
    ///     True when no installed <c>Available</c> dependent excludes <paramref name="candidate" /> (the candidate alone,
    ///     without the dependency updates it pulls in). Used to pick the highest allowed version of a held-back row.
    /// </summary>
    internal static bool IsAdmittedByInstalled(IReadOnlyCollection<CkModel> installed, CkModelId candidate) =>
        installed
            .Where(m => m.ModelState == ModelState.Available &&
                        !string.Equals(m.Id.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            .Select(FromInstalled)
            .All(d => Judge(d, candidate) == null);

    private static bool IsReplacement(IReadOnlyCollection<CkModel> installed, CkModelId candidate,
        bool includeServiceManaged)
    {
        if (!includeServiceManaged && CkModelLibraryStatusService.IsSystemManaged(candidate.Name))
        {
            return false;
        }

        // Not installed: nothing depends on it yet. ResolveFailed: the update may repair it (as the engine guard).
        return installed.Any(m => m.ModelState == ModelState.Available &&
                                  string.Equals(m.Id.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
                                  m.Id.Version.CompareTo(candidate.Version) != 0);
    }

    private static CkUpgradeBlockerDto? Judge(Requirements dependent, CkModelId candidate)
    {
        if (string.Equals(dependent.Id.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (dependent.Ranges != null)
        {
            // Range-retaining: only direct dependencies carry a range; a pin without one is transitive (AB#7940).
            var range = dependent.Ranges.FirstOrDefault(r =>
                string.Equals(r.Range.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
            if (range == null || Admits(range, candidate))
            {
                return null;
            }

            var floor = string.IsNullOrEmpty(range.Floor) ? string.Empty : $" (floor {range.Floor})";
            return Blocker(dependent, candidate, RangeKind, range.Range.FullName, RangeCode,
                $"range {range.Range.FullName}{floor} does not admit {candidate.FullName}");
        }

        var pin = dependent.Pins.FirstOrDefault(p =>
            string.Equals(p.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
        if (pin == null || pin.Version.CompareTo(candidate.Version) == 0)
        {
            return null;
        }

        return Blocker(dependent, candidate, ExactPinKind, pin.FullName, ExactPinCode,
            $"exact pin {pin.FullName}: {candidate.FullName} replaces it");
    }

    private static bool Admits(CkModelDependencyDto range, CkModelId candidate) =>
        string.IsNullOrEmpty(range.Floor)
            ? range.Range.IsSatisfiedBy(candidate)
            : range.IsSatisfiedBy(candidate);

    private static CkUpgradeBlockerDto Blocker(Requirements dependent, CkModelId candidate, string kind,
        string subject, string code, string reason) => new()
    {
        Dependent = dependent.Id.FullName,
        Update = candidate.FullName,
        Kind = kind,
        Subject = subject,
        Code = code,
        Message = $"{dependent.Id.Name} {dependent.Id.Version} would break: {reason}",
        DependentIsInUpdateSet = dependent.IsInUpdateSet
    };

    private static Requirements FromInstalled(CkModel model) =>
        new(model.Id, model.Dependencies ?? [],
            model.DependencyRanges?.Select(r => r.ToDto()).ToList(), false);

    private static Requirements FromCompiled(CkCompiledModelRoot model) =>
        new(model.ModelId, model.Dependencies ?? [],
            model.DependencyRanges?.ToList(), true);

    /// <summary>What one dependent requires: exact pins (classic) or the ranges of its direct dependencies.</summary>
    private sealed record Requirements(CkModelId Id, IReadOnlyCollection<CkModelId> Pins,
        IReadOnlyList<CkModelDependencyDto>? Ranges, bool IsInUpdateSet);
}
