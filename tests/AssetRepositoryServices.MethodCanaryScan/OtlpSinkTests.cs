using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     The OTLP sink of the scan: per-signal coverage of the collector's file exporter and the growth check the scan uses to
///     wait for the next metric export. Runs without the local stack.
/// </summary>
public sealed class OtlpSinkTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"canary-otlp-sink-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(true);

    [Fact]
    public void Coverage_SumsTheAppendedTextPerSignal()
    {
        var coverage = OtlpCoverage.From([
            new ScanText("otlp-export", OtlpCoverage.TracesFile, "abc"),
            new ScanText("otlp-export", OtlpCoverage.MetricsFile, "de"),
            new ScanText("otlp-export", "other.jsonl", "ignored")
        ]);

        Assert.Equal(new OtlpCoverage(3, 2, 0), coverage);
        Assert.Null(coverage.Missing);
    }

    [Fact]
    public void Coverage_WithoutLogs_IsComplete_BecauseTheServicesExportNoLogsOverOtlp()
    {
        var coverage = new OtlpCoverage(10, 10, 0);

        Assert.Null(coverage.Missing);
        Assert.Contains("logs none", coverage.Describe());
    }

    [Theory]
    [InlineData(0, 5, "no traces")]
    [InlineData(5, 0, "no metrics")]
    [InlineData(0, 0, "no traces and no metrics")]
    public void Coverage_WithoutTracesOrMetrics_NamesTheMissingSignal(long traces, long metrics, string expected)
    {
        var missing = new OtlpCoverage(traces, metrics, 0).Missing;

        Assert.NotNull(missing);
        Assert.Contains(expected, missing);
        Assert.Contains("OTLP_ENDPOINT_URL", missing);
    }

    [Fact]
    public void FileWindow_HasGrown_OnlyAfterTheFileChanged()
    {
        var metrics = Path.Combine(_directory.FullName, OtlpCoverage.MetricsFile);
        File.WriteAllText(metrics, "before");
        var window = new FileWindow("otlp-export", _directory, "*.jsonl");

        Assert.False(window.HasGrown(OtlpCoverage.MetricsFile));
        Assert.False(window.HasGrown(OtlpCoverage.TracesFile));

        File.AppendAllText(metrics, "+after");
        File.WriteAllText(Path.Combine(_directory.FullName, OtlpCoverage.TracesFile), "new");

        Assert.True(window.HasGrown(OtlpCoverage.MetricsFile));
        Assert.True(window.HasGrown(OtlpCoverage.TracesFile));
        var appended = window.ReadAppended(out var chars);
        Assert.Equal("+after", appended.Single(t => t.Source == OtlpCoverage.MetricsFile).Text);
        Assert.Equal("new", appended.Single(t => t.Source == OtlpCoverage.TracesFile).Text);
        Assert.Equal(9, chars);
    }

    [Fact]
    public void FileWindow_HasGrown_WhenTheFileWasTruncated()
    {
        var metrics = Path.Combine(_directory.FullName, OtlpCoverage.MetricsFile);
        File.WriteAllText(metrics, "a long first content");
        var window = new FileWindow("otlp-export", _directory, "*.jsonl");

        File.WriteAllText(metrics, "short");

        Assert.True(window.HasGrown(OtlpCoverage.MetricsFile));
        Assert.Equal("short", window.ReadAppended(out _).Single().Text);
    }
}
