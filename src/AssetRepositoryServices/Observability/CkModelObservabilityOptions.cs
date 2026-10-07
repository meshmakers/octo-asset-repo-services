namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     Configuration for the periodic CK model health sweep (AB#5432). Bound from the host's
///     <c>Observability:CkModel</c> section, so a cluster overrides it with
///     <c>OCTO_OBSERVABILITY__CKMODEL__*</c> environment variables.
/// </summary>
public sealed class CkModelObservabilityOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Observability:CkModel";

    /// <summary>
    ///     Master switch for the sweep. On by default: the per-tenant opt-in
    ///     (<c>System/TenantModeConfiguration.PublishCkModelObservability</c>) is what actually
    ///     decides whether anything is measured, and with no tenant opted in the sweep costs one
    ///     tenant enumeration plus one small read per tenant per interval. Switch this off to stop
    ///     the sweep entirely — the heartbeat then reports nothing at all rather than firing forever.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Interval between sweeps. Five minutes is a deliberate compromise: a CK model fault is a
    ///     standing condition, not a spike, and the catalog dependency walk behind a library-status
    ///     read is the expensive part of a sweep.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     One-off delay before the first sweep, so the sweep does not race the CK cache and the
    ///     catalog clients while the host is still coming up.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How long a sweep result stays publishable. Past this the fault gauges and the heartbeat
    ///     stop reporting and only <c>octo.ck.health.sweep.age</c> remains — which is the point:
    ///     stale faults must not read as current measurements. Default is three intervals, so a
    ///     single slow or skipped sweep does not flap the heartbeat.
    /// </summary>
    public TimeSpan StalenessWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Per-tenant budget for one library-status read. A tenant that exceeds it is reported as
    ///     unreachable rather than being allowed to stall the whole sweep — a stalled sweep is the
    ///     failure mode the heartbeat exists for, and it should not be reachable from one bad tenant.
    /// </summary>
    public TimeSpan PerTenantTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
