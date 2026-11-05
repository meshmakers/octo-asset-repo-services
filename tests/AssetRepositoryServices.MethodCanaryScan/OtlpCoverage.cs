using System.Globalization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     What the local OpenTelemetry collector received per signal while the scan ran. The collector of the scan writes one file
///     per signal (<c>traces.jsonl</c>, <c>metrics.jsonl</c>, <c>logs.jsonl</c>, see <c>otel-collector.yaml</c>).
/// </summary>
/// <remarks>
///     Traces and metrics are required: the services export both over OTLP (<c>ObservabilityBuilder</c>), so a scan without
///     either did not see that sink. Logs are not required: the services export no logs over OTLP (in the clusters the
///     collector reads the container output), and their log output is the service-log sink of the scan.
/// </remarks>
internal sealed record OtlpCoverage(long TraceChars, long MetricChars, long LogChars)
{
    public const string TracesFile = "traces.jsonl";
    public const string MetricsFile = "metrics.jsonl";
    public const string LogsFile = "logs.jsonl";

    /// <summary>Sums the appended text of the collector files by signal; unknown files count for no signal.</summary>
    public static OtlpCoverage From(IEnumerable<ScanText> otlpTexts)
    {
        long traces = 0, metrics = 0, logs = 0;
        foreach (var text in otlpTexts)
        {
            switch (text.Source)
            {
                case TracesFile:
                    traces += text.Text.Length;
                    break;
                case MetricsFile:
                    metrics += text.Text.Length;
                    break;
                case LogsFile:
                    logs += text.Text.Length;
                    break;
            }
        }

        return new OtlpCoverage(traces, metrics, logs);
    }

    /// <summary>Why the telemetry sink is incomplete (a required signal arrived empty), or null when traces and metrics arrived.</summary>
    public string? Missing
    {
        get
        {
            var missing = new List<string>();
            if (TraceChars == 0)
            {
                missing.Add("traces");
            }

            if (MetricChars == 0)
            {
                missing.Add("metrics");
            }

            return missing.Count == 0
                ? null
                : $"the collector received no {string.Join(" and no ", missing)} during the scan (stack started without " +
                  "OTLP_ENDPOINT_URL, or OCTO_CANARY_OTLP_METRICS_WAIT_SECONDS is shorter than the metric export interval; README.md)";
        }
    }

    /// <summary>One line for the report: characters per signal.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"traces {TraceChars:N0} chars, metrics {MetricChars:N0} chars, logs {(LogChars == 0 ? "none (the services export no logs over OTLP; their log output is the service-log sink)" : LogChars.ToString("N0", CultureInfo.InvariantCulture) + " chars")}");
}
