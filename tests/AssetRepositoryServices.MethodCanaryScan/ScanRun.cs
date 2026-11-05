using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>One call of the scan: what it exercised, what the caller got and what it expected.</summary>
internal sealed record ScanCase(string Name, string Canary, string Expected, string Actual)
{
    public bool AsExpected => Expected == "*" ? Actual != "SUCCESS" : Actual == Expected;
}

/// <summary>A canary form found in a sink. <see cref="Excerpt" /> shows the context with the hit replaced by its label.</summary>
internal sealed record ScanHit(string Sink, string Source, string Needle, string Excerpt);

/// <summary>
///     One scan against the local stack: the windows on the sinks are opened in the constructor, the calls are recorded with
///     <see cref="Record" />, <see cref="FinishAsync" /> reads every sink, searches every canary form and writes the report.
/// </summary>
internal sealed partial class ScanRun : IDisposable
{
    private readonly List<ScanCase> _cases = [];
    private readonly List<ScanText> _answers = [];
    private readonly List<(string Label, string Value)> _needles = [];
    private readonly FileWindow _logs;
    private readonly FileWindow? _otlp;
    private readonly List<string> _notCovered = [];

    public ScanRun(string title, ScanSettings settings)
    {
        Title = title;
        Settings = settings;
        Client = new StackClient(settings);
        StartedUtc = DateTime.UtcNow;
        _logs = new FileWindow("service-log", settings.LogDirectory, "*.log");
        _otlp = settings.OtlpDirectory is { Exists: true } otlp ? new FileWindow("otlp-export", otlp, "*.jsonl") : null;
        AddSecret("plain canary", Canaries.Plain);
        AddSecret("escaping canary", Canaries.Escaping);
        // the password-policy case sends the canaries in upper case
        AddSecret("escaping canary (upper case)", Canaries.Escaping.ToUpperInvariant());
        AddSecret("forged token", Canaries.ForgedToken);
    }

    public string Title { get; }

    public ScanSettings Settings { get; }

    public StackClient Client { get; }

    public DateTime StartedUtc { get; }

    /// <summary>Additional handler-host sink (asset-repo scan only).</summary>
    public CaptureSink? HandlerHost { get; set; }

    /// <summary>Adds every form of a secret value (canary, client secret) to the search.</summary>
    public void AddSecret(string label, string value)
    {
        foreach (var form in Canaries.Forms(value))
        {
            _needles.Add((label, form));
        }
    }

    /// <summary>Adds an access token used in the scan to the search (whole token and signature).</summary>
    public void AddToken(string label, string jwt)
    {
        foreach (var form in Canaries.TokenForms(jwt))
        {
            _needles.Add((label, form));
        }
    }

    public void NotCovered(string reason) => _notCovered.Add(reason);

    public void Record(string name, string canary, string expected, StackAnswer answer)
    {
        _cases.Add(new ScanCase(name, canary, expected, answer.Outcome));
        _answers.Add(new ScanText("graphql-answer", name, answer.Body));
    }

    public IReadOnlyList<ScanCase> Cases => _cases;

    /// <summary>
    ///     Why the telemetry sink was not scanned (no collector output configured, or the services exported nothing), or null.
    ///     A run without telemetry is not a clean run unless <c>OCTO_CANARY_ALLOW_NO_OTLP=1</c>.
    /// </summary>
    public string? TelemetryNotCaptured { get; private set; }

    /// <summary>Reads all sinks, searches them and writes <c>method-canary-scan-{slug}.md</c>. Returns the hits.</summary>
    public async Task<(IReadOnlyList<ScanHit> Hits, string ReportPath)> FinishAsync(string slug, string adminToken,
        CancellationToken ct)
    {
        // Services write their log asynchronously and the trace exporter batches (default schedule 5 s): wait for both.
        await Task.Delay(TimeSpan.FromSeconds(_otlp != null ? 12 : 4), ct);
        var texts = new List<ScanText>(_answers);
        texts.AddRange(_logs.ReadAppended(out var logBytes));
        long otlpBytes = 0;
        if (_otlp != null)
        {
            texts.AddRange(_otlp.ReadAppended(out otlpBytes));
            if (otlpBytes == 0)
            {
                TelemetryNotCaptured = "the collector received nothing during the scan: the services do not export telemetry " +
                                       "(start the stack with OCTO_OTLP_ENDPOINT_URL, README.md)";
            }
        }
        else
        {
            TelemetryNotCaptured = "OCTO_CANARY_OTLP_DIR is not set or does not exist (README.md)";
        }

        if (TelemetryNotCaptured != null)
        {
            NotCovered("Exported traces, metrics and logs (OTLP): " + TelemetryNotCaptured + ".");
        }

        var events = await Client.GetEventsSinceAsync(adminToken, StartedUtc, ct);
        texts.Add(new ScanText("audit-event", Settings.TenantId, events));
        if (HandlerHost != null)
        {
            texts.AddRange(HandlerHost.Texts);
        }

        var hits = Search(texts);
        var path = Path.Combine(Settings.ResultDirectory.FullName, $"method-canary-scan-{slug}.md");
        Settings.ResultDirectory.Create();
        await File.WriteAllTextAsync(path, Report(texts, hits, logBytes, otlpBytes, CountEvents(events)), ct);
        return (hits, path);
    }

    public void Dispose() => Client.Dispose();

    private List<ScanHit> Search(IEnumerable<ScanText> texts)
    {
        var hits = new List<ScanHit>();
        foreach (var text in texts)
        {
            foreach (var (label, form) in _needles)
            {
                var index = text.Text.IndexOf(form, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var from = Math.Max(0, index - 160);
                var to = Math.Min(text.Text.Length, index + form.Length + 160);
                var excerpt = text.Text[from..to].Replace(form, $"<{label}>", StringComparison.Ordinal);
                foreach (var (_, other) in _needles)
                {
                    excerpt = excerpt.Replace(other, "<secret>", StringComparison.Ordinal);
                }

                // the report is attached to work items: no part of a JWT (header, payload, signature) is ever printed
                excerpt = JwtPart().Replace(excerpt, "<jwt-part>");

                hits.Add(new ScanHit(text.Sink, text.Source, label, excerpt.ReplaceLineEndings(" ")));
            }
        }

        return hits.DistinctBy(h => (h.Sink, h.Source, h.Needle, h.Excerpt)).ToList();
    }

    private static int CountEvents(string json) => JsonNode.Parse(json) is JsonArray array ? array.Count : 0;

    private string Report(List<ScanText> texts, List<ScanHit> hits, long logBytes, long otlpBytes, int events)
    {
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# Method canary scan: {Title}");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"- Run: {StartedUtc:yyyy-MM-dd HH:mm:ss} UTC, tenant `{Settings.TenantId}`, asset repository {Settings.AssetRepositoryUri}, identity {Settings.IdentityUri}");
        b.AppendLine(CultureInfo.InvariantCulture, $"- Searched: {_needles.Count} forms of {_needles.Select(n => n.Label).Distinct().Count()} secret values (canaries, the access tokens of the scan, the secret of the scan's client)");
        b.AppendLine(CultureInfo.InvariantCulture, $"- **Result: {(hits.Count == 0 ? "0 hits" : hits.Count + " HITS")}**, {_cases.Count(c => !c.AsExpected)} case(s) with an unexpected outcome");
        b.AppendLine();
        b.AppendLine("## Calls");
        b.AppendLine();
        b.AppendLine("| Case | Canary | Expected | Caller got |");
        b.AppendLine("|------|--------|----------|------------|");
        foreach (var c in _cases)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"| {c.Name} | {c.Canary} | {(c.Expected == "*" ? "an error" : c.Expected)} | {c.Actual}{(c.AsExpected ? "" : " (UNEXPECTED)")} |");
        }

        b.AppendLine();
        b.AppendLine("## Sinks");
        b.AppendLine();
        b.AppendLine("| Sink | Scanned | Hits |");
        b.AppendLine("|------|---------|------|");
        b.AppendLine(CultureInfo.InvariantCulture, $"| GraphQL answers | {_answers.Count} answers | {hits.Count(h => h.Sink == "graphql-answer")} |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| Service log files (`{Settings.LogDirectory.FullName}`, appended bytes of every `*.log`) | {logBytes:N0} chars in {texts.Count(t => t.Sink == "service-log")} files ({string.Join(", ", texts.Where(t => t.Sink == "service-log").Select(t => t.Source))}) | {hits.Count(h => h.Sink == "service-log")} |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| Audit / event log of the tenant (System.Notification events) | {events} events | {hits.Count(h => h.Sink == "audit-event")} |");
        b.AppendLine(TelemetryNotCaptured != null
            ? "| Exported traces, metrics, logs (OTLP) | NOT CAPTURED: " + TelemetryNotCaptured + " | - |"
            : string.Create(CultureInfo.InvariantCulture, $"| Exported traces, metrics, logs (OTLP file exporter `{Settings.OtlpDirectory!.FullName}`) | {otlpBytes:N0} chars ({string.Join(", ", texts.Where(t => t.Sink == "otlp-export").Select(t => t.Source))}) | {hits.Count(h => h.Sink == "otlp-export")} |"));
        if (HandlerHost != null)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"| Scan handler host: log (`{HandlerHost.LogFile}`) | {texts.Count(t => t.Sink == "handler-host-log")} lines | {hits.Count(h => h.Sink == "handler-host-log")} |");
            b.AppendLine(CultureInfo.InvariantCulture, $"| Scan handler host: activities (all sources, all tags) | {texts.Count(t => t.Sink == "handler-host-activity")} spans | {hits.Count(h => h.Sink == "handler-host-activity")} |");
        }

        if (_notCovered.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("## Not covered");
            b.AppendLine();
            foreach (var reason in _notCovered)
            {
                b.AppendLine("- " + reason);
            }
        }

        b.AppendLine();
        b.AppendLine("## Hits");
        b.AppendLine();
        if (hits.Count == 0)
        {
            b.AppendLine("None.");
        }

        foreach (var hit in hits)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"- **{hit.Needle}** in {hit.Sink} `{hit.Source}`: `{hit.Excerpt.Replace("`", "'", StringComparison.Ordinal)}`");
        }

        return b.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("eyJ[A-Za-z0-9_-]{8,}(\\.[A-Za-z0-9_-]*){0,2}|[A-Za-z0-9_-]{40,}")]
    private static partial System.Text.RegularExpressions.Regex JwtPart();

    /// <summary>A random secret for the scan's own client; it is searched like a canary.</summary>
    public static string NewClientSecret() => "Canary-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
