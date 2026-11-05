namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     Where the scan finds the local stack. Everything comes from environment variables; secrets are never command-line
///     arguments. Defaults match <c>Start-Octo</c> on a developer machine.
/// </summary>
internal sealed class ScanSettings
{
    /// <summary>The scan runs only with <c>OCTO_METHOD_CANARY_SCAN=1</c>; otherwise every test is skipped.</summary>
    public const string GateVariable = "OCTO_METHOD_CANARY_SCAN";

    public static bool IsEnabled => Environment.GetEnvironmentVariable(GateVariable) == "1";

    public required string TenantId { get; init; }

    public required Uri IdentityUri { get; init; }

    public required Uri AssetRepositoryUri { get; init; }

    /// <summary>A client-credentials client that administers the tenant (the creator of a scratch tenant).</summary>
    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    /// <summary>The <c>logFiles</c> folder that <c>Start-Octo</c> writes the service output to.</summary>
    public required DirectoryInfo LogDirectory { get; init; }

    /// <summary>
    ///     Output folder of a local OpenTelemetry collector with the file exporter (<c>traces.jsonl</c>,
    ///     <c>metrics.jsonl</c>, <c>logs.jsonl</c>), or null when the services export no telemetry.
    /// </summary>
    public DirectoryInfo? OtlpDirectory { get; init; }

    /// <summary>
    ///     How long the scan waits after the last call before it reads the collector output: at least one metric export
    ///     interval of the services (the OpenTelemetry SDK exports metrics every 60 s by default, traces every 5 s), so every
    ///     service has exported the metrics that include the final calls. <c>OCTO_CANARY_OTLP_METRICS_WAIT_SECONDS</c>,
    ///     default 75.
    /// </summary>
    public TimeSpan OtlpMetricsWait { get; init; } = TimeSpan.FromSeconds(75);

    public required string BrokerHost { get; init; }

    public required ushort BrokerPort { get; init; }

    public required string BrokerUser { get; init; }

    public required string BrokerPassword { get; init; }

    public required string InstancePrefix { get; init; }

    /// <summary>Where the result report is written.</summary>
    public required DirectoryInfo ResultDirectory { get; init; }

    public static ScanSettings FromEnvironment()
    {
        var tenant = Required("OCTO_CANARY_TENANT");
        var clientId = Value("OCTO_CANARY_CLIENT_ID") ?? Required("OCTO_CLI_CLIENT_ID");
        var clientSecret = Value("OCTO_CANARY_CLIENT_SECRET") ?? Required("OCTO_CLI_CLIENT_SECRET");
        var logDirectory = Value("OCTO_CANARY_LOG_DIR") is { } logs ? new DirectoryInfo(logs) : FindLogDirectory();
        var otlp = Value("OCTO_CANARY_OTLP_DIR");
        return new ScanSettings
        {
            TenantId = tenant.ToLowerInvariant(),
            IdentityUri = new Uri(WithSlash(Value("OCTO_CANARY_IDENTITY_URL") ?? "https://localhost:5003/")),
            AssetRepositoryUri = new Uri(WithSlash(Value("OCTO_CANARY_ASSETREPO_URL") ?? "https://localhost:5001/")),
            ClientId = clientId,
            ClientSecret = clientSecret,
            LogDirectory = logDirectory,
            OtlpDirectory = otlp == null ? null : new DirectoryInfo(otlp),
            OtlpMetricsWait = TimeSpan.FromSeconds(int.Parse(Value("OCTO_CANARY_OTLP_METRICS_WAIT_SECONDS") ?? "75",
                System.Globalization.CultureInfo.InvariantCulture)),
            BrokerHost = Value("OCTO_CANARY_BROKER_HOST") ?? "localhost",
            BrokerPort = ushort.Parse(Value("OCTO_CANARY_BROKER_PORT") ?? "5672", System.Globalization.CultureInfo.InvariantCulture),
            BrokerUser = Value("OCTO_CANARY_BROKER_USER") ?? "guest",
            BrokerPassword = Value("OCTO_CANARY_BROKER_PASSWORD") ?? "guest",
            InstancePrefix = Value("OCTO_CANARY_INSTANCE_PREFIX") ?? "default",
            ResultDirectory = Directory.CreateDirectory(Value("OCTO_CANARY_RESULT_DIR") ?? AppContext.BaseDirectory)
        };
    }

    private static string? Value(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string Required(string name) =>
        Value(name) ?? throw new InvalidOperationException($"Environment variable {name} is not set (see README.md of the scan).");

    private static string WithSlash(string uri) => uri.EndsWith('/') ? uri : uri + "/";

    /// <summary>The lane's <c>logFiles</c> folder: the first ancestor of the test assembly that has one.</summary>
    private static DirectoryInfo FindLogDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = new DirectoryInfo(Path.Combine(directory.FullName, "logFiles"));
            if (candidate.Exists)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No logFiles folder found above the test assembly; set OCTO_CANARY_LOG_DIR.");
    }
}
