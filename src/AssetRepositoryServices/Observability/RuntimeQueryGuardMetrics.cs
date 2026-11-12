using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     OpenTelemetry instrument of the unbounded runtime-entity query guard (AB#8898). Counts every query at the API
///     boundary that the guard classifies as unbounded, in <c>Warn</c> and in <c>Enforce</c> (never in <c>Off</c>).
///     The Prometheus form of the name (Dash0 PromQL) is <c>octo_asset_runtime_query_unbounded_total</c>.
/// </summary>
/// <remarks>
///     Registered through the meter <see cref="CkModelObservabilityMetrics.MeterName" /> that the host already exports.
///     The meter is created by the <see cref="IMeterFactory" /> of the service provider, so each host (and each
///     integration-test provider) has its own instruments. Labels: <c>tenant</c>, <c>ck_type</c>, <c>client_id</c> (the
///     OAuth client id of the caller, <c>unknown</c> without one; never a token or a user name) and <c>mode</c>. All four
///     are bounded by the tenants, CK types and clients of an installation.
///     <c>ck_type</c> of <c>runtime.runtimeEntities</c> is the caller's argument only when it is a CK type of the tenant,
///     otherwise <see cref="UnknownCkType" />.
/// </remarks>
internal sealed class RuntimeQueryGuardMetrics
{
    /// <summary>Counter name. Contract with Dash0 queries and AB#8899; must not change.</summary>
    public const string UnboundedMetricName = "octo.asset.runtime_query.unbounded";

    /// <summary>Label names.</summary>
    public const string TenantLabel = "tenant";

    /// <inheritdoc cref="TenantLabel" />
    public const string CkTypeLabel = "ck_type";

    /// <inheritdoc cref="TenantLabel" />
    public const string ClientIdLabel = "client_id";

    /// <inheritdoc cref="TenantLabel" />
    public const string ModeLabel = "mode";

    /// <summary>Label value of <see cref="ClientIdLabel" /> when the caller carries no client id.</summary>
    public const string UnknownClientId = "unknown";

    /// <summary>
    ///     Label value of <see cref="CkTypeLabel" /> when the CK type id given to <c>runtime.runtimeEntities</c> is no CK
    ///     type of the tenant (malformed or unknown), so a caller cannot create a series per input.
    /// </summary>
    public const string UnknownCkType = "unknown";

    private readonly Counter<long> _unbounded;

    public RuntimeQueryGuardMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(new MeterOptions(CkModelObservabilityMetrics.MeterName) { Version = "1.0.0" });
        _unbounded = meter.CreateCounter<long>(
            UnboundedMetricName,
            unit: "{query}",
            description: "Runtime-entity queries at the asset repository API that select items without 'first' or " +
                         "with 'first' above RuntimeQueryGuard:MaxPageSize (AB#8898).");
    }

    /// <summary>Counts one unbounded query.</summary>
    public void RecordUnbounded(string tenantId, string ckTypeId, string? clientId, string mode) =>
        _unbounded.Add(1,
            new KeyValuePair<string, object?>(TenantLabel, tenantId),
            new KeyValuePair<string, object?>(CkTypeLabel, ckTypeId),
            new KeyValuePair<string, object?>(ClientIdLabel, string.IsNullOrEmpty(clientId) ? UnknownClientId : clientId),
            new KeyValuePair<string, object?>(ModeLabel, mode));
}
