using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     OpenTelemetry instruments for CK model health (AB#5432).
///
///     Until AB#5430 no self-emitted service metric could reach the backend at all: the meter
///     provider ended in <c>AddPrometheusExporter()</c> and nothing scraped that endpoint. CK model
///     health was therefore published by a blueprint pipeline that pushed OTLP itself, with client
///     mirroring and per-tenant <c>acr_values</c> because AB#5032/5077 closed cross-tenant tokens.
///     That was a way around a missing exporter, not a design. The asset repository already knows
///     every tenant's library status first-hand — no token, no mirror, no pipeline.
///
///     Static, mirroring <c>WorkloadLifecycleMetrics</c> in the communication controller and
///     <c>MongoCommandObservability</c> in the MongoDB engine: instruments are process-wide and
///     threading a metrics dependency through the sweep would add wiring without adding a seam.
///
///     <para>
///         <b>Fault-only, deliberately.</b> A healthy library emits nothing. The check rules in
///         meshmakers-infrastructure fire on <c>&gt;= threshold</c>, and a series that stops being
///         reported ages out on its own, which is what makes "repaired" observable without a
///         zero-value series per library per tenant.
///     </para>
///     <para>
///         <b>Which is exactly why the heartbeat exists.</b> With only faults on the wire, "the
///         whole fleet is healthy" and "the job is dead" look identical. On 29.09. the predecessor
///         stood still for four hours and nobody noticed. <see cref="SweepAgeSecondsMetricName" />
///         is the series that tells those apart, and it is a plain threshold like the other three
///         rules rather than an absence rule.
///     </para>
///     <para>
///         <b>Stale snapshots are not published.</b> An <see cref="ObservableGauge{T}" /> callback
///         fires on the exporter's interval, not on the sweep's, so a dead sweep would otherwise
///         keep re-publishing its last snapshot forever — the faults would look current and the
///         heartbeat would look alive. Every gauge except the age therefore reports nothing once the
///         last completed sweep is older than <see cref="StalenessWindow" />, and the age keeps
///         growing so the rule has something to fire on.
///     </para>
/// </summary>
internal static class CkModelObservabilityMetrics
{
    /// <summary>
    ///     Meter name. Registered in this service's own composition root (Program.cs) and in
    ///     octo-common-services' <c>ObservabilityBuilder</c> — see the comment on the local
    ///     registration for why both.
    /// </summary>
    public const string MeterName = "Meshmakers.Octo.AssetRepository";

    /// <summary>
    ///     Per-library fault state. <b>1</b> = catalog inconsistency (warning), <b>2</b> =
    ///     ResolveFailed (critical). Consumed by the <c>CkModelResolveFailed</c> and
    ///     <c>CkModelCatalogInconsistency</c> check rules; the name and the value scale are a
    ///     contract and must not change.
    /// </summary>
    public const string LibraryStateMetricName = "octo.ck.library.state";

    /// <summary>
    ///     1 while an opted-in tenant's library status could not be read at all. Consumed by the
    ///     <c>CkHealthTenantUnreachable</c> check rule; name is a contract.
    /// </summary>
    public const string TenantUnreachableMetricName = "octo.ck.tenant.unreachable";

    /// <summary>Unconditional liveness signal: 1 while the sweep is running on schedule.</summary>
    public const string SweepHeartbeatMetricName = "octo.ck.health.sweep";

    /// <summary>
    ///     Seconds since the last completed sweep — counted from process start until the first one
    ///     lands, so "never ran once" is as visible as "stopped running". This is the dead man's
    ///     switch: it never goes absent while the pod lives and it grows without bound, so the rule
    ///     is a threshold, not an absence check.
    /// </summary>
    public const string SweepAgeSecondsMetricName = "octo.ck.health.sweep.age";

    /// <summary>1 per tenant that the last completed sweep actually covered.</summary>
    public const string TenantObservedMetricName = "octo.ck.health.tenant.observed";

    /// <summary>State value for a catalog publishing inconsistency (warning).</summary>
    public const int StateCatalogInconsistency = 1;

    /// <summary>State value for a library in <c>ResolveFailed</c> (critical).</summary>
    public const int StateResolveFailed = 2;

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    ///     Last sweep result per tenant. Replaced wholesale per tenant per sweep, so a repaired
    ///     library disappears from the map — and therefore from the export — on the next sweep.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TenantSnapshot> Tenants = new();

    /// <summary>
    ///     UTC ticks of the last completed sweep, or of process start before the first one.
    ///     <see cref="Interlocked" />-accessed: written by the sweep, read by exporter threads.
    /// </summary>
    private static long _lastSweepUtcTicks = DateTime.UtcNow.Ticks;

    private static long _armed;

    /// <summary>
    ///     How long a snapshot stays publishable. Set once from
    ///     <c>CkModelObservabilityOptions.StalenessWindow</c> when the sweep starts; the default is
    ///     only a safety net for a host that never arms the sweep at all.
    /// </summary>
    public static TimeSpan StalenessWindow { get; set; } = TimeSpan.FromMinutes(15);

    // Assigning the gauges to fields is what keeps them alive; the callbacks do the reporting.
    // ReSharper disable NotAccessedField.Local
    private static readonly ObservableGauge<int> LibraryStateGauge = Meter.CreateObservableGauge(
        LibraryStateMetricName,
        ObserveLibraryStates,
        unit: "{state}",
        description:
        "CK model library fault state of a tenant: 1 = catalog publishing inconsistency (a pinned " +
        "dependency is not available in any registered catalog), 2 = the installed model is in " +
        "ResolveFailed. A healthy library is not reported at all");

    private static readonly ObservableGauge<int> TenantUnreachableGauge = Meter.CreateObservableGauge(
        TenantUnreachableMetricName,
        ObserveTenantUnreachable,
        unit: "{tenant}",
        description:
        "1 while the CK model library status of an opted-in tenant could not be read. A reachable " +
        "tenant is not reported at all");

    private static readonly ObservableGauge<int> SweepHeartbeatGauge = Meter.CreateObservableGauge(
        SweepHeartbeatMetricName,
        ObserveHeartbeat,
        unit: "{sweep}",
        description:
        "1 while the CK model health sweep is completing on schedule. Absent once the last " +
        "completed sweep is older than the staleness window — paired with " +
        SweepAgeSecondsMetricName + ", which stays present and is the series to alert on");

    private static readonly ObservableGauge<double> SweepAgeGauge = Meter.CreateObservableGauge(
        SweepAgeSecondsMetricName,
        ObserveSweepAge,
        unit: "s",
        description:
        "Seconds since the CK model health sweep last completed, counted from process start until " +
        "the first completed sweep. The dead man's switch for a fault-only metric family: it grows " +
        "without bound when the sweep stops, so a threshold rule catches a stalled, stuck or " +
        "never-started sweep");

    private static readonly ObservableGauge<int> TenantObservedGauge = Meter.CreateObservableGauge(
        TenantObservedMetricName,
        ObserveTenantsObserved,
        unit: "{tenant}",
        description:
        "1 per tenant that the last completed sweep covered, i.e. per tenant with " +
        "System/TenantModeConfiguration.PublishCkModelObservability = true. Distinguishes " +
        "\"no faults\" from \"nothing was looked at\"");
    // ReSharper restore NotAccessedField.Local

    /// <summary>
    ///     Arms the instruments. Until the sweep calls this, every gauge reports nothing — a
    ///     deployment that switched the sweep off must not produce a dead man's switch that fires
    ///     forever.
    /// </summary>
    public static void Arm(TimeSpan stalenessWindow)
    {
        StalenessWindow = stalenessWindow;
        Interlocked.Exchange(ref _armed, 1);
    }

    /// <summary>
    ///     Publishes one tenant's result. <paramref name="faults" /> replaces whatever the previous
    ///     sweep found for this tenant, so repaired libraries stop being exported.
    /// </summary>
    public static void SetTenantFaults(string tenantId, IReadOnlyList<CkLibraryFault> faults)
    {
        Tenants[tenantId] = new TenantSnapshot(false, faults);
    }

    /// <summary>
    ///     Publishes that an opted-in tenant could not be read. Deliberately clears the tenant's
    ///     faults: an unreadable tenant has no current fault list, and keeping the previous one
    ///     would report stale faults as if they had just been measured.
    /// </summary>
    public static void SetTenantUnreachable(string tenantId)
    {
        Tenants[tenantId] = new TenantSnapshot(true, []);
    }

    /// <summary>
    ///     Drops a tenant from the export — it opted out, or it is gone. Without this an opt-out
    ///     would leave its last faults on the wire until the pod restarts.
    /// </summary>
    public static void Forget(string tenantId)
    {
        Tenants.TryRemove(tenantId, out _);
    }

    /// <summary>
    ///     Marks a sweep as completed. Only a completed sweep refreshes the heartbeat: a sweep that
    ///     threw halfway through has not observed the tenants it never reached, and treating it as
    ///     alive is the failure this whole heartbeat exists to prevent.
    /// </summary>
    public static void RecordSweepCompleted()
    {
        Interlocked.Exchange(ref _lastSweepUtcTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Test seam: drops all state so instrument assertions start from a known point.</summary>
    internal static void ResetForTests()
    {
        Tenants.Clear();
        Interlocked.Exchange(ref _armed, 0);
        Interlocked.Exchange(ref _lastSweepUtcTicks, DateTime.UtcNow.Ticks);
        StalenessWindow = TimeSpan.FromMinutes(15);
    }

    /// <summary>Seconds since the last completed sweep (or since process start).</summary>
    internal static double SweepAgeSeconds()
    {
        var ticks = Interlocked.Read(ref _lastSweepUtcTicks);
        var age = DateTime.UtcNow.Ticks - ticks;
        return age <= 0 ? 0d : new TimeSpan(age).TotalSeconds;
    }

    private static bool IsArmed => Interlocked.Read(ref _armed) == 1;

    private static bool IsFresh => IsArmed && SweepAgeSeconds() <= StalenessWindow.TotalSeconds;

    private static IEnumerable<Measurement<int>> ObserveLibraryStates()
    {
        if (!IsFresh)
        {
            yield break;
        }

        foreach (var (tenantId, snapshot) in Tenants)
        {
            foreach (var fault in snapshot.Faults)
            {
                yield return new Measurement<int>(fault.State,
                    new KeyValuePair<string, object?>("octo.tenant.id", tenantId),
                    new KeyValuePair<string, object?>("octo.ck.library", fault.Library),
                    new KeyValuePair<string, object?>("octo.ck.version", fault.Version));
            }
        }
    }

    private static IEnumerable<Measurement<int>> ObserveTenantUnreachable()
    {
        if (!IsFresh)
        {
            yield break;
        }

        foreach (var (tenantId, snapshot) in Tenants)
        {
            if (snapshot.Unreachable)
            {
                yield return new Measurement<int>(1,
                    new KeyValuePair<string, object?>("octo.tenant.id", tenantId));
            }
        }
    }

    private static IEnumerable<Measurement<int>> ObserveHeartbeat()
    {
        if (IsFresh)
        {
            yield return new Measurement<int>(1);
        }
    }

    private static IEnumerable<Measurement<double>> ObserveSweepAge()
    {
        // Not gated on freshness — this is the series that has to survive the sweep dying.
        if (IsArmed)
        {
            yield return new Measurement<double>(SweepAgeSeconds());
        }
    }

    private static IEnumerable<Measurement<int>> ObserveTenantsObserved()
    {
        if (!IsFresh)
        {
            yield break;
        }

        foreach (var tenantId in Tenants.Keys)
        {
            yield return new Measurement<int>(1,
                new KeyValuePair<string, object?>("octo.tenant.id", tenantId));
        }
    }

    private sealed record TenantSnapshot(bool Unreachable, IReadOnlyList<CkLibraryFault> Faults);
}

/// <summary>
///     One reportable CK model fault: the library, the version the fault belongs to, and the state
///     value from <see cref="CkModelObservabilityMetrics" />.
/// </summary>
/// <param name="Library">CK model name, e.g. <c>EnergyIQ</c>.</param>
/// <param name="Version">
///     The version the fault is about — the installed one for a ResolveFailed, the catalog one for a
///     publishing inconsistency. Carried as a label so "still broken at the same version" and "broke
///     again after a bump" are different series rather than one series changing value.
/// </param>
/// <param name="State">
///     <see cref="CkModelObservabilityMetrics.StateResolveFailed" /> or
///     <see cref="CkModelObservabilityMetrics.StateCatalogInconsistency" />.
/// </param>
internal sealed record CkLibraryFault(string Library, string Version, int State);
