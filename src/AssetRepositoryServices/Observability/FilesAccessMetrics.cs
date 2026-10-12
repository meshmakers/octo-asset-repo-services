using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     OpenTelemetry instrument for refused Files API requests (AB#6575). The Files API is fail-closed
///     (AB#6467): a caller without FileViewer / FileManagement (or an equivalent data permission) gets 403
///     FORBIDDEN. Without a signal a pipeline or service account that lacks the file roles fails silently
///     after a rollout; the Dash0 rule in meshmakers-infrastructure fires on this counter.
///
///     <para>
///         <b>Fault-only.</b> Only denials are counted, so the series set is bounded by the number of
///         principals that are actually refused - the <c>account</c> label (subject id of the user or the
///         client id, never a token) therefore stays small and is what lets the alert name the account.
///     </para>
///     Registered through the same meter as <see cref="CkModelObservabilityMetrics" />.
/// </summary>
internal static class FilesAccessMetrics
{
    /// <summary>Counter name. Contract with the Dash0 rule; must not change.</summary>
    public const string AccessDeniedMetricName = "octo.files.access_denied";

    /// <summary>Label values of the <c>action</c> attribute.</summary>
    public const string ActionRead = "read";

    /// <summary>Label value for create / rename / move.</summary>
    public const string ActionWrite = "write";

    /// <summary>Label value for delete.</summary>
    public const string ActionDelete = "delete";

    /// <summary>Label value for a user token issued for another tenant than the route tenant (AB#6579).</summary>
    public const string ActionTenantMismatch = "tenant_mismatch";

    private static readonly Meter Meter = new(CkModelObservabilityMetrics.MeterName, "1.0.0");

    private static readonly Counter<long> AccessDenied = Meter.CreateCounter<long>(
        AccessDeniedMetricName,
        unit: "{denial}",
        description: "Files API requests refused with 403 FORBIDDEN because the caller has no file role / data permission.");

    /// <summary>Counts one refused request.</summary>
    public static void RecordAccessDenied(string tenantId, string account, string action) =>
        AccessDenied.Add(1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("account", account),
            new KeyValuePair<string, object?>("action", action));
}
