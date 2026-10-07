using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     Turns a tenant's library status into the fault list that goes on the wire (AB#5432).
///
///     Separate from both the sweep and the metrics for one reason: this is the only part with
///     judgement in it, and it is pure — a DTO in, a fault list out, no clock, no tenant, no
///     repository. Everything that could be argued about in a review is therefore unit-testable
///     without a Mongo container.
/// </summary>
internal static class CkModelHealthEvaluator
{
    /// <summary>
    ///     <c>ModelState</c> string for a library the engine could not resolve. The DTO carries
    ///     <c>ModelState.ToString()</c>, not the enum, because it is a REST contract.
    /// </summary>
    private const string ResolveFailedState = "ResolveFailed";

    private const string UnknownVersion = "unknown";

    /// <summary>
    ///     Evaluates one tenant's library status.
    ///
    ///     <para>
    ///         <b>One fault per library, highest severity wins.</b> A library can be both in
    ///         <c>ResolveFailed</c> and unresolvable from the catalog, and the two would then carry
    ///         different versions and produce two series with two different values for the same
    ///         library — which reads as a contradiction on a dashboard. ResolveFailed wins: it is the
    ///         condition that is actually broken in the tenant right now, while the catalog
    ///         inconsistency describes why the repair is not available.
    ///     </para>
    ///     <para>
    ///         <b>Catalog inconsistency is only reported for installed libraries.</b> A
    ///         <c>HasCatalogInconsistency</c> row for a model the tenant never installed is a
    ///         publishing problem in a shared catalog, not a tenant problem — and reporting it per
    ///         tenant would multiply one broken publish by the number of opted-in tenants and make
    ///         the series say something it does not mean. Installed-and-unrepairable is the
    ///         actionable case, and it is the one that stays.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<CkLibraryFault> Evaluate(CkModelLibraryStatusResponseDto status)
    {
        var faults = new List<CkLibraryFault>();

        foreach (var item in status.Items)
        {
            var isInstalled = !string.IsNullOrEmpty(item.InstalledVersion);

            if (string.Equals(item.ModelState, ResolveFailedState, StringComparison.Ordinal))
            {
                faults.Add(new CkLibraryFault(item.Name,
                    item.InstalledVersion ?? item.CatalogVersion ?? UnknownVersion,
                    CkModelObservabilityMetrics.StateResolveFailed));
                continue;
            }

            if (item.HasCatalogInconsistency && isInstalled)
            {
                faults.Add(new CkLibraryFault(item.Name,
                    item.CatalogVersion ?? item.InstalledVersion ?? UnknownVersion,
                    CkModelObservabilityMetrics.StateCatalogInconsistency));
            }
        }

        return faults;
    }
}
